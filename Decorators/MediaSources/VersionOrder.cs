using System.Globalization;
using Gelato.RemuxDb;
using Gelato.Services;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Gelato.Decorators.MediaSources;

/// <summary>
/// Which of a movie's or an episode's streams goes first: the one the user is part way through,
/// or the one of the binge group the episode before it was played with.
/// </summary>
internal sealed class VersionOrder(
    MediaSourceManagerDecorator decorator,
    ILibraryManager libraryManager,
    IUserDataManager userDataManager
)
{
    private readonly ILibraryManager _libraryManager = libraryManager;

    /// <summary>
    /// Puts the stream the user is part way through first, so clients preselect it. The resume
    /// point itself is shared: StreamUserDataSync copies it to the movie.
    /// </summary>
    internal void MoveResumedVersionFirst(
        List<MediaSourceInfo> sources,
        Video primary,
        List<(Video Row, MediaSourceInfo Source)> versions,
        User user
    )
    {
        if (sources.Count < 2 || versions.Count == 0)
            return;

        // The source with the movie's id plays with the movie's own watch state.
        var primaryId = primary.Id.ToString("N", CultureInfo.InvariantCulture);
        var streams = versions.Where(v => v.Source.Id != primaryId).ToList();
        var userData = userDataManager.GetUserDataBatch(
            [primary, .. streams.Select(v => v.Row)],
            user
        );
        if (userData.GetValueOrDefault(primary.Id) is not { PlaybackPositionTicks: > 0 } movieData)
            return;

        var resumed = VersionPlaybackSelector.SelectMostRecentlyPlayed(
            streams,
            v => userData.GetValueOrDefault(v.Row.Id),
            data => data.PlaybackPositionTicks > 0
        );

        // The movie holds a copy of the stream's state, dated CopyOffset after the stream; it is
        // newer only when the stream with the movie's id was played since.
        if (
            resumed.Source is not { } source
            || (userData[resumed.Row.Id].LastPlayedDate ?? DateTime.MinValue)
                + StreamUserDataSync.CopyOffset
                < (movieData.LastPlayedDate ?? DateTime.MinValue)
            || ReferenceEquals(sources[0], source)
        )
        {
            return;
        }

        sources.Remove(source);
        sources.Insert(0, source);
    }

    // The binge group an episode follows, per user: the group of the stream the episode before it
    // is played with. Kept in memory only: it is for the episode that plays next, and playing the
    // episode before it sets it again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        (Guid User, Guid Item),
        (string Group, DateTime SetAt)
    > _bingeGroups = new();

    /// <summary>
    /// Makes <paramref name="next"/> follow the binge group of the stream <paramref name="played"/>
    /// is played with: Stremio's hint that streams of different episodes belong together (same
    /// addon, quality and release group). Its stream of that group becomes its first stream: the
    /// one listed first and published under the episode's id, which is what clients preselect,
    /// what autoplay asks for (Jellyfin Web names the episode's id as the source) and what is
    /// pre-probed. A stream without a group, or a local file, leaves the next episode as it is.
    /// </summary>
    public void FollowBingeGroup(BaseItem played, string? mediaSourceId, BaseItem next, User user)
    {
        var key = (user.Id, next.Id);
        var group = FindPlayedRow(played, mediaSourceId, user)?.GelatoData<string>("bingeGroup");
        if (string.IsNullOrEmpty(group))
        {
            _bingeGroups.TryRemove(key, out _);
            return;
        }

        // Not all at once: the stream under an episode's id must not change while it plays.
        if (_bingeGroups.Count > 1000)
        {
            foreach (var (stale, _) in _bingeGroups.OrderBy(g => g.Value.SetAt).Take(500))
                _bingeGroups.TryRemove(stale, out _);
        }

        _bingeGroups[key] = (group, DateTime.UtcNow);
    }

    /// <summary>The stream row a playback of the item plays, null for a file.</summary>
    private Video? FindPlayedRow(BaseItem played, string? mediaSourceId, User user)
    {
        var item = Guid.TryParse(mediaSourceId, out var sourceId)
            ? _libraryManager.GetItemById(sourceId) ?? played
            : played;
        if (item.IsStreamRow())
            return item as Video;

        // The source with the episode's id is its first stream, whichever row that is.
        var itemId = item.Id.ToString("N", CultureInfo.InvariantCulture);
        var source = decorator.GetStaticMediaSources(item, false, user).FirstOrDefault(s => s.Id == itemId);
        return
            Guid.TryParse(source?.ETag, out var rowId)
            && _libraryManager.GetItemById(rowId) is Video row
            && row.IsStreamRow()
            ? row
            : null;
    }

    /// <summary>
    /// Puts the stream of the binge group the episode follows ahead of its other streams, which
    /// keep the addon's order.
    /// </summary>
    internal void MoveBingeGroupFirst(
        List<(Video Row, MediaSourceInfo Source)> versions,
        Video primary,
        Guid userId
    )
    {
        if (versions.Count < 2 || !_bingeGroups.TryGetValue((userId, primary.Id), out var follows))
            return;

        var at = versions.FindIndex(v => v.Row.GelatoData<string>("bingeGroup") == follows.Group);
        if (at < 1 || IsShortClip(versions[at].Row, primary))
            return;

        var version = versions[at];
        versions.RemoveAt(at);
        versions.Insert(0, version);
    }

    private static readonly TimeSpan ShortClip = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShortClipTitle = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether a row's probe found a clip of under a minute for a title of over 15 minutes: the
    /// video a debrid service serves for a file it does not have (8 or 30 s; 17 of 117 probed
    /// movie rows on a production library). Such a row is listed after the others, so it is
    /// neither the default nor what playback moves on to before them. Its name stays as the addon
    /// sent it, and it is not hidden: the title may have nothing else.
    /// </summary>
    internal static bool IsShortClip(BaseItem row, BaseItem? primary) =>
        primary?.RunTimeTicks > ShortClipTitle.Ticks
        && row.RunTimeTicks is long ticks
        && ticks > 0
        && ticks < ShortClip.Ticks
        && row.GelatoData<string>("mediaInfo") == RemuxDbService.SourceProbe;
}
