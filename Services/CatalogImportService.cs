using System.Collections.Concurrent;
using System.Diagnostics;
using Gelato.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
        var seriesFolder = cfg.SeriesFolder;
        var movieFolder = cfg.MovieFolder;

        if (seriesFolder is null)
        {
            logger.LogWarning("No series root folder found");
        }

        if (movieFolder is null)
        {
            logger.LogWarning("No movie root folder found");
        }

        var maxItems = catalogCfg.MaxItems;

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
                "{Id}: processed {Count} items: {Created} new, {Existing} already in the library, {Skipped} skipped, {Failed} failed",
                catalogCfg.Id,
                processedItems,
                created,
                existing,
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

        var total = enabled.Sum(c => c.MaxItems);
        var offset = 0;

        foreach (var cat in enabled)
        {
            ct.ThrowIfCancellationRequested();

            var catMax = cat.MaxItems;
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
