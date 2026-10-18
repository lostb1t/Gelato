using System.Collections.Concurrent;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;

namespace Gelato.Services;

/// <summary>
/// The version each SyncPlay group plays of a movie/episode, so every member plays the same one.
/// </summary>
/// <remarks>
/// SyncPlay hands its members item ids only. A member that plays a movie by its id gets the first
/// version, whatever the others play, and versions differ in length, which breaks the position
/// sync. Jellyfin does not expose which group a session is in, so <see cref="Filters.SyncPlayFilter"/>
/// records it from the create, join and leave requests. Both maps live as long as Jellyfin's own
/// groups, which do not survive a restart either.
/// </remarks>
public sealed class SyncPlaySources(Lazy<ISessionManager> sessionManager)
{
    private readonly ConcurrentDictionary<string, Guid> _groups = new();
    private readonly ConcurrentDictionary<(Guid Group, Guid Item), string> _sources = new();

    public void Joined(string sessionId, Guid groupId)
    {
        _groups[sessionId] = groupId;

        // A session that ended left its group without a request.
        var live = sessionManager.Value.Sessions.Select(s => s.Id).ToHashSet();
        foreach (var id in _groups.Keys.Where(id => !live.Contains(id)))
            _groups.TryRemove(id, out _);

        var groups = _groups.Values.ToHashSet();
        foreach (var key in _sources.Keys.Where(k => !groups.Contains(k.Group)))
            _sources.TryRemove(key, out _);
    }

    public void Left(string sessionId) => _groups.TryRemove(sessionId, out _);

    /// <summary>
    /// A new queue is a new pick: Jellyfin Web names the picked version by its own id there.
    /// </summary>
    public void QueueSet(string sessionId)
    {
        if (!_groups.TryGetValue(sessionId, out var group))
            return;

        foreach (var key in _sources.Keys.Where(k => k.Group == group))
            _sources.TryRemove(key, out _);
    }

    public void Choose(Guid groupId, Guid itemId, string sourceId) =>
        _sources[(groupId, itemId)] = sourceId;

    public Guid? GroupOf(HttpContext ctx) =>
        SessionOf(ctx) is { } session && _groups.TryGetValue(session.Id, out var group)
            ? group
            : null;

    /// <summary>The version the requesting session's group plays of the item, if it chose one.</summary>
    public string? SourceFor(HttpContext ctx, Guid itemId)
    {
        if (_sources.IsEmpty || GroupOf(ctx) is not { } group)
            return null;

        return _sources.TryGetValue((group, itemId), out var id) ? id : null;
    }

    /// <summary>
    /// The requesting user's session on the request's device, as Jellyfin's SyncPlay controller
    /// finds it.
    /// </summary>
    public SessionInfo? SessionOf(HttpContext ctx)
    {
        var deviceId = ctx.User.FindFirst("Jellyfin-DeviceId")?.Value;
        var client = ctx.User.FindFirst("Jellyfin-Client")?.Value;
        if (string.IsNullOrEmpty(deviceId) || !ctx.TryGetUserId(out var userId))
            return null;

        return sessionManager.Value.Sessions.FirstOrDefault(s =>
            s.UserId == userId
            && string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal)
            && string.Equals(s.Client, client, StringComparison.Ordinal)
        );
    }
}
