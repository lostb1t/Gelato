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

        var catalogFolder = manager.TryGetCatalogFolder(catalogCfg);

        // The catalog names a folder that is not there: not in a library yet (the scan that adds it
        // is still running) or not reachable. New items go to the movie and series folders, and
        // nothing is moved: without this, a folder that is gone for a while would send everything
        // the catalog has in it back to the default folders.
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
        var leaveAlone = GetFoldersToLeaveAlone(cfg, catalogFolder);

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

                                        if (
                                            !isNew
                                            && !folderMissing
                                            && await MoveIntoAsync(item, root, leaveAlone, innerCt)
                                                .ConfigureAwait(false)
                                        )
                                        {
                                            Interlocked.Increment(ref moved);
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

            if (catalogCfg.CreateCollection)
            {
                var itemIds = importedIds.Values.Where(id => id != Guid.Empty).ToList();
                await UpdateCollectionAsync(catalogCfg, itemIds.Take(100).ToList(), itemIds.Count)
                    .ConfigureAwait(false);
            }

            // Skipped: listed twice in the catalog, or a type without a library folder.
            logger.LogInformation(
                "{Id}: processed {Count} items: {Created} new, {Existing} already in the library ({Moved} moved into the catalog's folder), {Skipped} skipped, {Failed} failed",
                catalogCfg.Id,
                processedItems,
                created,
                existing,
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
    /// Folders an item a catalog lists is not taken out of: the folder of another catalog, since the
    /// first catalog to claim an item keeps it and two catalogs must not pass it back and forth,
    /// and the per-user folders, whose items belong to that user. The movie and series folders are
    /// not among them, and neither is a folder no catalog uses any more: an item there moves.
    /// </summary>
    private HashSet<Guid> GetFoldersToLeaveAlone(PluginConfiguration cfg, Folder? target)
    {
        var ids = manager.GetCatalogFolders(cfg).Select(f => f.Id).ToHashSet();

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

    /// <summary>
    /// Moves an item the catalog lists into the folder the catalog imports into, when it is
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
            item is not (Movie or Series)
            || item.ParentId == target.Id
            || leaveAlone.Contains(item.ParentId)
            || item.HasStreamTag()
            || !(item.Path?.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase) ?? false)
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
