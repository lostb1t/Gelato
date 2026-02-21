using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities.Movies;
using System.Diagnostics;
using Gelato.Config;

// For BoxSet

namespace Gelato.Services;

public class CatalogImportService(
    ILogger<CatalogImportService> logger,
    GelatoManager manager,
    CatalogService catalogService,
    ICollectionManager collectionManager,
    ILibraryManager libraryManager) {
    public async Task ImportCatalogAsync(string catalogId, string type, CancellationToken ct, IProgress<double>? progress = null) {
        var catalogCfg = catalogService.GetCatalogConfig(catalogId, type);
        if (catalogCfg == null) {
            logger.LogWarning("Catalog config not found for {Id} {Type}", catalogId, type);
            return;
        }

        if (!catalogCfg.Enabled) {
            logger.LogInformation("Catalog {Id} {Type} is disabled, skipping.", catalogId, type);
            return;
        }
        var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
        var stremio = cfg.Stremio;
        var seriesFolder = cfg.SeriesFolder;
        var movieFolder = cfg.MovieFolder;

        if (seriesFolder is null) {
            logger.LogWarning("No series root folder found");
        }

        if (movieFolder is null) {
            logger.LogWarning("No movie root folder found");
        }


        var maxItems = catalogCfg.MaxItems > 0 ? catalogCfg.MaxItems : cfg.CatalogMaxItems;
        long done = 0;

        var opts = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };

        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Starting import for catalog {Name} ({Id}) - Limit: {Limit}", catalogCfg.Name, catalogId, maxItems);

        try {
            var skip = 0;
            var processed = 0;
            var importedIds = new ConcurrentBag<Guid>();

            while (processed < maxItems) {
                ct.ThrowIfCancellationRequested();

                var page = await stremio
                    .GetCatalogMetasAsync(catalogId, type, search: null, skip: skip)
                    .ConfigureAwait(false);

                if (page.Count == 0) {
                    break;
                }

                await Parallel.ForEachAsync(
                    page,
                    opts,
                    async (meta, ctInner) => {
                        var p = Interlocked.Increment(ref processed);
                        ctInner.ThrowIfCancellationRequested();
                        if (p > maxItems) {
                            return;
                        }

                        //meta_ids.TryAdd(meta.Id, 0);
                        var mediaType = meta.Type;
                        var baseItemKind = mediaType.ToBaseItem();

                        // catalog can contain multiple types.

                        var root =
                            baseItemKind switch
                            {
                                BaseItemKind.Series => seriesFolder,
                                BaseItemKind.Movie => movieFolder,
                                _ => null
                            };

                        if (root is not null) {
                            try {
                                var (item, _) = await manager
                                    .InsertMeta(
                                        root,
                                        meta,
                                        null,
                                        true,
                                        true,
                                        baseItemKind == BaseItemKind.Series,
                                        ctInner
                                    )
                                    .ConfigureAwait(false);

                                if (item != null) importedIds.Add(item.Id);
                            }
                            catch (Exception ex) {
                                logger.LogError(
                                    "{CatId}: insert meta failed for {Id}. Exception: {Message}\n{StackTrace}",
                                    catalogId,
                                    meta.Id,
                                    ex.Message,
                                    ex.StackTrace
                                );
                            }
                        }

                        var current = Interlocked.Increment(ref done);
                        progress?.Report(Math.Min(100, current / (double)maxItems * 100.0));
                    }
                );

                skip += page.Count;
            }

            if (catalogCfg.CreateCollection) {
                await UpdateCollectionAsync(catalogCfg, importedIds.ToList())
                    .ConfigureAwait(false);
                importedIds.Clear();
            }

            logger.LogInformation("{Id}: processed ({Count} items)", catalogCfg.Id, processed);
        }
        catch (OperationCanceledException ex) {
            logger.LogWarning(
                ex,
                "Catalog {Id} aborted due to non-user cancellation, continuing with next catalog",
                    catalogId
            );
        }
        catch (Exception ex) {
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
            "Catalog {catalog} sync completed in {Minutes}m {Seconds}s ({TotalSeconds:F2}s total)",
            catalogCfg.Name,
            (int)stopwatch.Elapsed.TotalMinutes,
            stopwatch.Elapsed.Seconds,
            stopwatch.Elapsed.TotalSeconds
        );
    }

    private async Task<BoxSet?> GetOrCreateBoxSetAsync(CatalogConfig config) {
        var id = $"{config.Type}.{config.Id}";
        var collection = libraryManager.GetItemList(new InternalItemsQuery {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            CollapseBoxSetItems = false,
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string> { { "Stremio", id } }
        }).OfType<BoxSet>().FirstOrDefault();

        if (collection is null) {
            collection = await collectionManager.CreateCollectionAsync(new CollectionCreationOptions {
                Name = config.Name,
                IsLocked = true,
                ProviderIds = new Dictionary<string, string> { { "Stremio", id } }
            }).ConfigureAwait(false);

            collection.DisplayOrder = "Default";
            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        }
        return collection;
    }

    private async Task UpdateCollectionAsync(CatalogConfig config, List<Guid> ids) {
        try {
            var collection = await GetOrCreateBoxSetAsync(config).ConfigureAwait(false);
            if (collection != null) {

                var currentChildren = libraryManager.GetItemList(new InternalItemsQuery {
                    Parent = collection,
                    Recursive = false
                }).Select(i => i.Id).ToList();

                if (currentChildren.Count != 0) {
                    await collectionManager.RemoveFromCollectionAsync(collection.Id, currentChildren).ConfigureAwait(false);
                }

                var itemsToAdd = ids.ToList();

                await collectionManager.AddToCollectionAsync(collection.Id, itemsToAdd).ConfigureAwait(false);
                logger.LogInformation("Updated collection {Name} with {Count} items", config.Name, itemsToAdd.Count);
            }
        }
        catch (Exception ex) {
            logger.LogError(ex, "Error updating collection for {Name}", config.Name);
        }
    }

    public async Task SyncAllEnabledAsync(CancellationToken ct, IProgress<double>? progress = null) {
        // Force refresh of catalog list from manifest first
        var catalogs = await catalogService.GetCatalogsAsync(Guid.Empty);
        var enabled = catalogs.Where(c => c.Enabled).ToList();

        var current = 0;
        var total = enabled.Count;

        foreach (var cat in enabled) {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Processing enabled catalog: {Name}", cat.Name);

            // Create specific progress reporter for this catalog if needed, or just log
            await ImportCatalogAsync(cat.Id, cat.Type, ct).ConfigureAwait(false);

            current++;
            progress?.Report((double)current / total * 100);
        }

        progress?.Report(100);
        await libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
    }
}
