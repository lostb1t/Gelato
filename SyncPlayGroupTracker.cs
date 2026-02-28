using System;
using System.Collections.Concurrent;

namespace Gelato;

/// <summary>
/// Tracks which SyncPlay group each user belongs to.
/// Populated by <see cref="Filters.SyncPlayGroupFilter"/> when users
/// create, join, or leave SyncPlay groups.
/// </summary>
internal static class SyncPlayGroupTracker
{
    private static readonly ConcurrentDictionary<Guid, Guid> _userGroups = new();

    public static void SetGroup(Guid userId, Guid groupId) =>
        _userGroups[userId] = groupId;

    public static void RemoveUser(Guid userId) =>
        _userGroups.TryRemove(userId, out _);

    public static Guid? GetGroupForUser(Guid userId) =>
        _userGroups.TryGetValue(userId, out var g) ? g : null;
}
