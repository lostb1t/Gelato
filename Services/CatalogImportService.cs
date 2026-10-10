using System.Collections.Concurrent;
using System.Diagnostics;
using Gelato.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

// For BoxSet

namespace Gelato.Services;

public class CatalogImportService(
    ILogger<CatalogImportService> logger,
    GelatoManager manager,
    CatalogService catalogService,
    ICollectionManager collectionManager,
    ILibraryManager libraryManager
)
{
    private static readonly TimeSpan FolderWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The catalog's folder. A library that was picked a moment ago has the folder among its
    /// locations before the scan has made it an item: the import waits for that scan, up to a
    /// minute, instead of importing into the default folders as it does for a folder that is in
    /// no library at all.
    /// </summary>
    private async Task<Folder?> WaitForCatalogFolderAsync(
        CatalogConfig catalog,
        CancellationToken ct
    )
    {
        var folder = manager.TryGetCatalogFolder(catalog);
        if (folder is not null || string.IsNullOrWhiteSpace(catalog.Path))
            return folder;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var waited = Stopwatch.StartNew();
        while (
            folder is null
            && waited.Elapsed < FolderWait
            && libraryManager
                .GetVirtualFolders()
                .Any(v => v.Locations.Any(l => string.Equals(l, catalog.Path, comparison)))
        )
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            folder = manager.TryGetCatalogFolder(catalog);
        }

        if (folder is not null)
        {
            logger.LogInformation(
                "Catalog {Name}: waited {Seconds:F0}s for the scan that adds {Path} to its library",
                catalog.Name,
                waited.Elapsed.TotalSeconds,
                catalog.Path
            );
        }

        return folder;
    }

    public async Task ImportCatalogAsync(
        string catalogId,
        string type,
        CancellationToken ct,
        IProgress<double>? progress = null
    )
    {
        var catalogCfg = catalogService.GetCatalogConfig(catalogId, type);
        if (catalogCfg == null)
        {
            logger.LogWarning("Catalog config not found for {Id} {Type}", catalogId, type);
            return;
        }

        if (!catalogCfg.Enabled)
        {
            logger.LogInformation("Catalog {Id} {Type} is disabled, skipping.", catalogId, type);
            return;
        }
        var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
        var stremio = cfg.Stremio;

        var catalogFolder = await WaitForCatalogFolderAsync(catalogCfg, ct).ConfigureAwait(false);

        // The catalog names a folder that is not there: in no library, or not reachable. New
        // items go to the movie and series folders, and nothing is moved: without this, a folder
        // that is gone for a while would send everything the catalog has in it back to the
        // default folders.
        var folderMissing = catalogFolder is null && !string.IsNullOrWhiteSpace(catalogCfg.Path);
        if (folderMissing)
        {
            logger.LogWarning(
                "Catalog {Name}: folder {Path} is not in a Jellyfin library, importing new items into the movie and series folders and moving nothing",
                catalogCfg.Name,
                catalogCfg.Path
            );
        }

        // A catalog's folder takes the kind its library is for: movies in a shows library, or the
        // other way round, are not listed by it. A mixed library takes both. The other kind goes
        // to its default folder, as for a catalog without a folder.
        var seriesFolder =
            catalogFolder is not null && manager.LibraryTakes(catalogFolder, BaseItemKind.Series)
                ? catalogFolder
                : cfg.SeriesFolder;
        var movieFolder =
            catalogFolder is not null && manager.LibraryTakes(catalogFolder, BaseItemKind.Movie)
                ? catalogFolder
                : cfg.MovieFolder;
        var catalogKey = catalogCfg.GetKey();
        var catalogKeys = cfg.Catalogs.Select(c => c.GetKey()).ToHashSet();
        var foldersOfOthers = GetFoldersOfOthers(cfg, catalogFolder, otherCatalogs: true);
        var userFolders = GetFoldersOfOthers(cfg, catalogFolder, otherCatalogs: false);

        if (seriesFolder is null)
        {
            logger.LogWarning("No series root folder found");
        }

        if (movieFolder is null)
        {
            logger.LogWarning("No movie root folder found");
        }

        var maxItems = catalogCfg.GetMaxItems(cfg.CatalogMaxItems);

        var stopwatch = Stopwatch.StartNew();
        var outcome = "failed";
        logger.LogInformation(
            "Starting import for catalog {Name} ({Id}) - Limit: {Limit}",
            catalogCfg.Name,
            catalogId,
            maxItems
        );

        try
        {
            var skip = 0;
            var processedItems = 0;
            var created = 0;
            var existing = 0;
            var claimed = 0;
            var moved = 0;
            var skipped = 0;
            var failed = 0;
            // keyed on stremio meta.Id to deduplicate within the import run
            var importedIds = new ConcurrentDictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            while (processedItems < maxItems)
            {
                ct.ThrowIfCancellationRequested();

                var page = await stremio
                    .GetCatalogMetasAsync(catalogId, type, search: null, skip: skip)
                    .ConfigureAwait(false);

                if (page.Count == 0)
                {
                    break;
                }

                var remaining = maxItems - processedItems;
                var batch = page.Take(remaining).ToList();

                await Parallel
                    .ForEachAsync(
                        batch,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = 4,
                            CancellationToken = ct,
                        },
                        async (meta, innerCt) =>
                        {
                            if (!importedIds.TryAdd(meta.Id, Guid.Empty))
                            {
                                Interlocked.Increment(ref skipped);
                                Interlocked.Increment(ref processedItems);
                                return;
                            }

                            var mediaType = meta.Type;
                            var baseItemKind = mediaType.ToBaseItem();

                            // catalog can contain multiple types.
                            var root = baseItemKind switch
                            {
                                BaseItemKind.Series => seriesFolder,
                                BaseItemKind.Movie => movieFolder,
                                _ => null,
                            };

                            if (root is not null)
                            {
                                try
                                {
                                    var (item, isNew) = await manager
                                        .InsertMeta(
                                            root,
                                            meta,
                                            null,
                                            true,
                                            true,
                                            baseItemKind == BaseItemKind.Series,
                                            innerCt
                                        )
                                        .ConfigureAwait(false);

                                    if (item != null)
                                    {
                                        importedIds[meta.Id] = item.Id;
                                        Interlocked.Increment(
                                            ref isNew ? ref created : ref existing
                                        );

                                        // The catalog's own from now on, also one that was in
                                        // the library and is no catalog's yet: what is the
                                        // catalog's moves with it when its library changes.
                                        if (
                                            await ClaimAsync(
                                                    item,
                                                    catalogKey,
                                                    catalogKeys,
                                                    foldersOfOthers,
                                                    innerCt
                                                )
                                                .ConfigureAwait(false)
                                            && !isNew
                                        )
                                        {
                                            Interlocked.Increment(ref claimed);
                                        }
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref failed);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.Increment(ref failed);
                                    logger.LogError(
                                        "{CatId}: insert meta failed for {Id}. Exception: {Message}\n{StackTrace}",
                                        catalogId,
                                        meta.Id,
                                        ex.Message,
                                        ex.StackTrace
                                    );
                                }
                            }
                            else
                            {
                                Interlocked.Increment(ref skipped);
                            }

                            var done = Interlocked.Increment(ref processedItems);
                            progress?.Report(done * 100.0 / maxItems);
                        }
                    )
                    .ConfigureAwait(false);

                skip += page.Count;
            }

            // What is the catalog's goes where the catalog imports into. After its library was
            // set, changed or cleared that is all of it, also what an earlier import brought in
            // with a higher limit: found by its mark, without asking the addon.
            if (!folderMissing)
            {
                foreach (var item in manager.GetCatalogItems(catalogKey))
                {
                    var target = item is Series ? seriesFolder : movieFolder;
                    if (
                        target is not null
                        && await MoveIntoAsync(item, target, userFolders, ct).ConfigureAwait(false)
                    )
                    {
                        moved++;
                    }
                }
            }

            if (catalogCfg.CreateCollection)
            {
                var itemIds = importedIds.Values.Where(id => id != Guid.Empty).ToList();
                await UpdateCollectionAsync(catalogCfg, itemIds.Take(100).ToList(), itemIds.Count)
                    .ConfigureAwait(false);
            }

            // Skipped: listed twice in the catalog, or a type without a library folder.
            logger.LogInformation(
                "{Id}: processed {Count} items: {Created} new, {Existing} already in the library ({Claimed} of them the catalog's from now on), {Moved} moved into the catalog's folder, {Skipped} skipped, {Failed} failed",
                catalogCfg.Id,
                processedItems,
                created,
                existing,
                claimed,
                moved,
                skipped,
                failed
            );
            outcome = "completed";
        }
        catch (OperationCanceledException ex)
        {
            outcome = "aborted";
            logger.LogWarning(
                ex,
                "Catalog {Id} aborted due to non-user cancellation, continuing with next catalog",
                catalogId
            );
        }
        catch (Exception ex)
        {
            outcome = "failed";
            logger.LogError(
                ex,
                "Catalog sync failed for {Id}: {Message}",
                catalogCfg.Id,
                ex.Message
            );
        }

        stopwatch.Stop();
        progress?.Report(100);
        logger.LogInformation(
            "Catalog {catalog} sync {Outcome} after {Minutes}m {Seconds}s ({TotalSeconds:F2}s total)",
            catalogCfg.Name,
            outcome,
            (int)stopwatch.Elapsed.TotalMinutes,
            stopwatch.Elapsed.Seconds,
            stopwatch.Elapsed.TotalSeconds
        );
    }

    /// <summary>
    /// Folders whose items are not this catalog's to take: the per-user folders, whose items
    /// belong to that user, and with <paramref name="otherCatalogs"/> the folders of the other
    /// catalogs. An item there is not made the catalog's own; one that is the catalog's already
    /// follows it out of another catalog's folder, but not out of a user's. The movie and series
    /// folders are not among them, and neither is a folder no catalog uses any more.
    /// </summary>
    private HashSet<Guid> GetFoldersOfOthers(
        PluginConfiguration cfg,
        Folder? target,
        bool otherCatalogs
    )
    {
        var ids = otherCatalogs
            ? manager.GetCatalogFolders(cfg).Select(f => f.Id).ToHashSet()
            : [];

        foreach (var user in cfg.UserConfigs)
        {
            var userCfg = GelatoPlugin.Instance!.GetConfig(user.UserId);
            if (userCfg.MovieFolder is { } userMovies)
                ids.Add(userMovies.Id);
            if (userCfg.SeriesFolder is { } userSeries)
                ids.Add(userSeries.Id);
        }

        if (target is not null)
            ids.Remove(target.Id);
        if (cfg.MovieFolder is { } movies)
            ids.Remove(movies.Id);
        if (cfg.SeriesFolder is { } series)
            ids.Remove(series.Id);

        return ids;
    }

    /// <summary>Gelato's own placeholder for a title: not a local file, not a stream row.</summary>
    private static bool IsPlaceholder(BaseItem item) =>
        item is Movie or Series
        && !item.HasStreamTag()
        && (item.Path?.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Makes an item the import created or found the catalog's own. One a catalog has already
    /// stays with that catalog: the first to import a title keeps it, so two catalogs that list it
    /// do not pass it back and forth. One that was in the library before catalogs kept their
    /// items, or came in by a search, is taken by the first import that lists it within its limit.
    /// </summary>
    private async Task<bool> ClaimAsync(
        BaseItem item,
        string catalogKey,
        IReadOnlySet<string> catalogKeys,
        HashSet<Guid> foldersOfOthers,
        CancellationToken ct
    )
    {
        if (!IsPlaceholder(item) || foldersOfOthers.Contains(item.ParentId))
            return false;

        return await manager
            .ClaimForCatalogAsync(item, catalogKey, catalogKeys, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Moves an item that is the catalog's into the folder the catalog imports into, when it is
    /// somewhere else. Setting a catalog's folder, changing it or clearing it takes effect on
    /// items imported before that on the next sync. Only Gelato's own placeholders move: a local
    /// file stays where the library put it.
    /// </summary>
    private async Task<bool> MoveIntoAsync(
        BaseItem item,
        Folder target,
        HashSet<Guid> leaveAlone,
        CancellationToken ct
    )
    {
        if (
            !IsPlaceholder(item)
            || item.ParentId == target.Id
            || leaveAlone.Contains(item.ParentId)
        )
        {
            return false;
        }

        try
        {
            return await manager.MoveToFolderAsync(item, target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not move {Name} ({Id}) into {Folder}",
                item.Name,
                item.Id,
                target.Path
            );
            return false;
        }
    }

    private async Task<BoxSet?> GetOrCreateBoxSetAsync(CatalogConfig config)
    {
        var id = $"{config.Type}.{config.Id}";
        var collection = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.BoxSet],
                    CollapseBoxSetItems = false,
                    Recursive = true,
                    HasAnyProviderId = new Dictionary<string, string> { { "Stremio", id } },
                }
            )
            .OfType<BoxSet>()
            .FirstOrDefault();

        if (collection is null)
        {
            collection = await collectionManager
                .CreateCollectionAsync(
                    new CollectionCreationOptions
                    {
                        Name = config.Name,
                        IsLocked = true,
                        ProviderIds = new Dictionary<string, string> { { "Stremio", id } },
                    }
                )
                .ConfigureAwait(false);

            collection.DisplayOrder = "Default";
            await collection
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }
        return collection;
    }

    private async Task UpdateCollectionAsync(CatalogConfig config, List<Guid> ids, int available)
    {
        // A collection holds at most 100 of the catalog's items.
        logger.LogInformation(
            "Updating collection {Name} with {Count} of {Available} items",
            config.Name,
            ids.Count,
            available
        );
        try
        {
            var collection = await GetOrCreateBoxSetAsync(config).ConfigureAwait(false);
            if (collection != null)
            {
                var currentChildren = libraryManager
                    .GetItemList(new InternalItemsQuery { Parent = collection, Recursive = false })
                    .Select(i => i.Id)
                    .ToList();

                if (currentChildren.Count != 0)
                {
                    await collectionManager
                        .RemoveFromCollectionAsync(collection.Id, currentChildren)
                        .ConfigureAwait(false);
                }

                await collectionManager
                    .AddToCollectionAsync(collection.Id, ids)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating collection for {Name}", config.Name);
        }
    }

    public async Task SyncAllEnabledAsync(CancellationToken ct, IProgress<double>? progress = null)
    {
        var catalogs = await catalogService.GetCatalogsAsync(Guid.Empty);
        var enabled = catalogs.Where(c => c.Enabled).ToList();

        if (enabled.Count == 0)
        {
            progress?.Report(100);
            return;
        }

        var globalMaxItems = GelatoPlugin.Instance!.Configuration.CatalogMaxItems;
        var total = enabled.Sum(c => c.GetMaxItems(globalMaxItems));
        var offset = 0;

        foreach (var cat in enabled)
        {
            ct.ThrowIfCancellationRequested();

            var catMax = cat.GetMaxItems(globalMaxItems);
            var localOffset = offset;
            var catProgress = progress is null
                ? null
                : (IProgress<double>)
                    new Progress<double>(p =>
                        progress.Report((localOffset + p / 100.0 * catMax) / total * 100.0)
                    );

            await ImportCatalogAsync(cat.Id, cat.Type, ct, catProgress).ConfigureAwait(false);

            offset += catMax;
        }

        // collections appear empty after inporting this fixes that.. sometimes...
        libraryManager.QueueLibraryScan();

        progress?.Report(100);
    }
}
