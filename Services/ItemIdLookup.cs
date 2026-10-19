using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Gelato.Services;

/// <summary>
/// Item ids found through an index, for queries Jellyfin would otherwise answer with a scan.
/// </summary>
/// <remarks>
/// Jellyfin turns a tag filter into a correlated EXISTS on every row the rest of the query leaves,
/// and a provider id into <c>ProviderId || ':' || ProviderValue</c>, which no index serves. The
/// stream rows below a series were found by walking every non-folder item, people included:
/// 65 ms a lookup on a library of 35k items, and a search with an owned series ran four. Asking
/// for the ids first and loading those by primary key takes about a millisecond, and most lookups
/// find no ids at all.
/// </remarks>
public sealed class ItemIdLookup(IDbContextFactory<JellyfinDbContext> dbFactory)
{
    private static readonly string StreamTagCleanValue = GelatoManager.StreamTag.GetCleanValue();

    /// <summary>The ids of the stream rows below any of <paramref name="ancestorIds"/>.</summary>
    public Guid[] StreamRowsUnder(IReadOnlyList<Guid> ancestorIds)
    {
        using var db = dbFactory.CreateDbContext();
        return db
            .AncestorIds.AsNoTracking()
            .Where(a => ancestorIds.Contains(a.ParentItemId))
            .Join(db.ItemValuesMap, a => a.ItemId, m => m.ItemId, (a, m) => m)
            .Where(m =>
                m.ItemValue.Type == ItemValueType.Tags
                && m.ItemValue.CleanValue == StreamTagCleanValue
            )
            .Select(m => m.ItemId)
            .Distinct()
            .ToArray();
    }

    /// <summary>The ids of the items tagged <paramref name="tag"/>.</summary>
    public Guid[] WithTag(string tag)
    {
        var clean = tag.GetCleanValue();
        using var db = dbFactory.CreateDbContext();
        return db
            .ItemValuesMap.AsNoTracking()
            .Where(m => m.ItemValue.Type == ItemValueType.Tags && m.ItemValue.CleanValue == clean)
            .Select(m => m.ItemId)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// The ids of the items whose <paramref name="provider"/> id is <paramref name="value"/>.
    /// </summary>
    public Guid[] WithProviderId(string provider, string value)
    {
        using var db = dbFactory.CreateDbContext();
        return db
            .BaseItemProviders.AsNoTracking()
            .Where(p => p.ProviderId == provider && p.ProviderValue == value)
            .Select(p => p.ItemId)
            .ToArray();
    }

    /// <summary>
    /// Which of <paramref name="itemIds"/> have alternate versions: their stream rows, and whether
    /// any version is something else (one merged in by hand, a second file).
    /// </summary>
    /// <remarks>
    /// One query for a whole page. Jellyfin counts a title's versions by loading every one of them
    /// and asking each for its own local versions: 2 N + 3 commands for a title with N stream rows,
    /// about a thousand for a listing of a library in which 60 titles had been opened.
    /// </remarks>
    public IReadOnlyDictionary<Guid, VersionOwner> VersionOwners(IReadOnlyList<Guid> itemIds)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<Guid, VersionOwner>();
        }

        using var db = dbFactory.CreateDbContext();
        var links = db
            .LinkedChildren.AsNoTracking()
            .Where(l =>
                itemIds.Contains(l.ParentId)
                && (
                    l.ChildType == LinkedChildType.LinkedAlternateVersion
                    || l.ChildType == LinkedChildType.LocalAlternateVersion
                )
            )
            .Select(l => new
            {
                l.ParentId,
                l.ChildId,
                IsStream = l.ChildType == LinkedChildType.LinkedAlternateVersion
                    && db.ItemValuesMap.Any(m =>
                        m.ItemId == l.ChildId
                        && m.ItemValue.Type == ItemValueType.Tags
                        && m.ItemValue.CleanValue == StreamTagCleanValue
                    ),
            })
            .ToArray();

        var owners = new Dictionary<Guid, VersionOwner>();
        foreach (var link in links)
        {
            if (!owners.TryGetValue(link.ParentId, out var owner))
            {
                owners[link.ParentId] = owner = new VersionOwner();
            }

            if (link.IsStream)
            {
                owner.StreamRows.Add(link.ChildId);
            }
            else
            {
                owner.HasOtherVersions = true;
            }
        }

        return owners;
    }

    /// <summary>Which of <paramref name="itemIds"/> hold user data of <paramref name="userId"/>.</summary>
    public IReadOnlySet<Guid> WithUserData(Guid userId, IReadOnlyList<Guid> itemIds)
    {
        if (itemIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        using var db = dbFactory.CreateDbContext();
        return db
            .UserData.AsNoTracking()
            .Where(u => u.UserId == userId && itemIds.Contains(u.ItemId))
            .Select(u => u.ItemId)
            .Distinct()
            .ToHashSet();
    }

    /// <summary>The versions of a movie/episode, as <see cref="VersionOwners"/> found them.</summary>
    public sealed class VersionOwner
    {
        public List<Guid> StreamRows { get; } = [];

        public bool HasOtherVersions { get; set; }
    }
}
