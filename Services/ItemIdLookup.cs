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
}
