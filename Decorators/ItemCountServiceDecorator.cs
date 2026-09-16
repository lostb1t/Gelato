using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;

namespace Gelato.Decorators;

/// <summary>
/// Keeps Gelato's stream rows out of the episode and played counts of seasons and series.
/// </summary>
/// <remarks>
/// On 10.11 a folder counted its episodes through <see cref="IItemRepository"/>, where
/// <see cref="GelatoItemRepository"/> hides the <c>gelato-stream</c> rows. Jellyfin 12 counts
/// straight on the database in <see cref="IItemCountService"/>, so every stream row of a season
/// was counted as an episode: a season with 8 episodes and 65 loaded streams showed 73 episodes,
/// and never showed as watched. The counts are taken as Jellyfin computes them, minus the stream
/// rows below each folder.
/// </remarks>
public sealed class ItemCountServiceDecorator(IItemCountService inner, IItemRepository repo)
    : IItemCountService
{
    public int GetCount(InternalItemsQuery filter) => inner.GetCount(filter);

    public ItemCounts GetItemCounts(InternalItemsQuery filter) => inner.GetItemCounts(filter);

    public ItemCounts GetItemCountsForNameItem(
        BaseItemKind kind,
        Guid id,
        BaseItemKind[] relatedItemKinds,
        InternalItemsQuery accessFilter
    ) => inner.GetItemCountsForNameItem(kind, id, relatedItemKinds, accessFilter);

    public Dictionary<Guid, ItemCounts> GetItemCountsForNameItems(
        BaseItemKind kind,
        IReadOnlyList<Guid> ids,
        BaseItemKind[] relatedItemKinds,
        InternalItemsQuery accessFilter
    ) => inner.GetItemCountsForNameItems(kind, ids, relatedItemKinds, accessFilter);

    public int GetPlayedCount(InternalItemsQuery filter, Guid ancestorId) =>
        GetPlayedAndTotalCount(filter, ancestorId).Played;

    public int GetTotalCount(InternalItemsQuery filter, Guid ancestorId)
    {
        var total = inner.GetTotalCount(filter, ancestorId);
        if (total == 0)
            return 0;

        return Math.Max(0, total - GetStreamRows([ancestorId], filter.User).Count);
    }

    public (int Played, int Total) GetPlayedAndTotalCount(
        InternalItemsQuery filter,
        Guid ancestorId
    )
    {
        var counts = inner.GetPlayedAndTotalCount(filter, ancestorId);
        if (counts.Total == 0)
            return counts;

        var streams = GetStreamRows([ancestorId], filter.User);
        if (streams.Count == 0)
            return counts;

        var played = GetPlayedIds(streams, filter.User).Count;
        return (Math.Max(0, counts.Played - played), Math.Max(0, counts.Total - streams.Count));
    }

    // Not corrected: a collection that links a whole series still counts that series' stream rows.
    public (int Played, int Total) GetPlayedAndTotalCountFromLinkedChildren(
        InternalItemsQuery filter,
        Guid parentId
    ) => inner.GetPlayedAndTotalCountFromLinkedChildren(filter, parentId);

    public Dictionary<Guid, (int Played, int Total)> GetPlayedAndTotalCountBatch(
        IReadOnlyList<Guid> folderIds,
        User user
    )
    {
        var result = inner.GetPlayedAndTotalCountBatch(folderIds, user);
        if (result.Count == 0)
            return result;

        var streams = GetStreamRows([.. result.Keys], user);
        if (streams.Count == 0)
            return result;

        var playedIds = GetPlayedIds(streams, user);
        foreach (var stream in streams)
        {
            var isPlayed = playedIds.Contains(stream.Id);
            // Walking the parents is only needed to tell several folders apart.
            IEnumerable<Guid> ancestorIds =
                result.Count == 1 ? result.Keys : stream.GetAncestorIds().Distinct();
            foreach (var ancestorId in ancestorIds.ToList())
            {
                if (!result.TryGetValue(ancestorId, out var counts))
                    continue;

                result[ancestorId] = (
                    Math.Max(0, counts.Played - (isPlayed ? 1 : 0)),
                    Math.Max(0, counts.Total - 1)
                );
            }
        }

        // Jellyfin leaves folders without any countable item out of the batch.
        foreach (var (id, counts) in result.ToList())
        {
            if (counts is { Played: 0, Total: 0 })
                result.Remove(id);
        }

        return result;
    }

    public Dictionary<Guid, int> GetChildCountBatch(IReadOnlyList<Guid> parentIds, User? user)
    {
        var result = inner.GetChildCountBatch(parentIds, user);
        if (result.Count == 0)
            return result;

        // Only direct children count here: the episodes of a season by SeasonId, and by ParentId
        // the items that sit in no season. Since Jellyfin 12.1 the access filter applies here as
        // well, so the rows linked as alternate versions are already left out.
        var streams = GetStreamRows(parentIds, user);
        foreach (var stream in streams)
        {
            var parentId =
                stream is Episode { SeasonId: var seasonId } && seasonId != Guid.Empty
                    ? seasonId
                    : stream.ParentId;

            if (result.TryGetValue(parentId, out var count))
                result[parentId] = Math.Max(0, count - 1);
        }

        return result;
    }

    /// <summary>
    /// The stream rows below any of <paramref name="ancestorIds"/> that
    /// <paramref name="user"/> may see, which is what Jellyfin counted for them. Rows linked as
    /// alternate versions are left out, as Jellyfin's access filter leaves them out of the counts.
    /// </summary>
    private List<BaseItem> GetStreamRows(IReadOnlyList<Guid> ancestorIds, User? user)
    {
        if (ancestorIds.Count == 0)
            return [];

        return repo.GetItemList(StreamRowsQuery(ancestorIds, user))
            .Where(i => i.HasStreamTag())
            .ToList();
    }

    private HashSet<Guid> GetPlayedIds(IReadOnlyList<BaseItem> streams, User? user)
    {
        if (user is null)
            return [];

        var query = StreamRowsQuery([], user);
        query.ItemIds = [.. streams.Select(s => s.Id)];
        query.IsPlayed = true;
        return [.. repo.GetItemIdsList(query)];
    }

    private static InternalItemsQuery StreamRowsQuery(
        IReadOnlyList<Guid> ancestorIds,
        User? user
    ) =>
        new(user)
        {
            AncestorIds = [.. ancestorIds],
            Tags = [GelatoManager.StreamTag],
            IsFolder = false,
            IsVirtualItem = false,
            Recursive = true,
            GroupByPresentationUniqueKey = false,
            // Marks the lookup as internal, so GelatoItemRepository does not hide the stream rows.
            IsDeadPerson = true,
            DtoOptions = new DtoOptions(false) { EnableImages = false },
        };
}
