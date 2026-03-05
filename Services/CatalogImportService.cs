using Gelato.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Gelato.Services;

public class CatalogImportService(
    ILogger<CatalogImportService> logger,
    GelatoManager manager,
    CatalogService catalogService,
    ICollectionManager collectionManager,
    ILibraryManager libraryManager)
{
    private const string ProviderKey = "Stremio";

    public async Task ImportCatalogAsync(
        string catalogId,
        string type,
        CancellationToken ct,
        IProgress<double>? progress = null)
    {
        var catalogCfg = ConfigurationHelper.GetCatalogConfig(catalogId, type);
        if (catalogCfg == null)
        {
            logger.LogWarning("Catalog config not found for {CatalogId} {Type}", catalogId, type);
            return;
        }

        if (!catalogCfg.Enabled)
        {
            logger.LogInformation("Catalog {CatalogId} {Type} is disabled, skipping.", catalogId, type);
            return;
        }

        if (catalogCfg.MaxItems <= 0)
        {
            logger.LogWarning(
                "{MaxItemsName} for {Name} (ID: {CatalogId}) ({Type}) is {MaxItems}, skipping.",
                nameof(catalogCfg.MaxItems),
                catalogCfg.Name,
                catalogId,
                type,
                catalogCfg.MaxItems);
            return;
        }

        var cfg = ConfigurationHelper.GetConfig();
        var catalogName = $"{catalogCfg.Name} {catalogCfg.Type}";

        var stremio = cfg.Stremio;
        if (stremio == null)
        {
            logger.LogError("Unable to retrieve AIOStreams from configuration, aborting Import. Please check the AIOStreams URL in your settings.");
            return;
        }

        logger.LogInformation(
            "Starting import for catalog {CatalogName} ({CatalogId}) ({Type}) - Max Items: {MaxItems}",
            catalogName, catalogId, type, catalogCfg.MaxItems
        );

        var stopwatch = Stopwatch.StartNew();

        var skip = 0;
        var processedItems = 0;
        var maxItems = catalogCfg.MaxItems;

        var allSeenLibraryCatalogItems = new List<BaseItem>();

        var stuckPages = 0;
        const int maxStuckPages = 2;
        string[]? lastPageCatalogItemIds = null;

        try
        {
            while (processedItems < maxItems)
            {
                ct.ThrowIfCancellationRequested();

                var catalogItems = await stremio
                    .GetCatalogMetasAsync(catalogId, type, search: null, skip: skip)
                    .ConfigureAwait(false);

                if (catalogItems.Count == 0)
                {
                    break;
                }

                var catalogPageItemIds = catalogItems
                   .Select(x => x.Id)
                   .Where(id => !string.IsNullOrWhiteSpace(id))
                   .ToArray();

                if (lastPageCatalogItemIds != null && lastPageCatalogItemIds.SequenceEqual(catalogPageItemIds))
                {
                    if (stuckPages >= maxStuckPages)
                    {
                        logger.LogWarning(
                            "Stopping import for {CatalogName} {CatalogId} ({Type}) to avoid loop.",
                            catalogName, catalogId, type);
                        break;
                    }

                    stuckPages++;

                    logger.LogWarning(
                        "Item retrieval for {CatalogName} {CatalogId} ({Type}) appears stuck, attemping retry {Attempt}/{MaxAttempt}.",
                        catalogName, catalogId, type, stuckPages, maxStuckPages);

                    continue;
                }
                else
                {
                    stuckPages = 0;
                    lastPageCatalogItemIds = catalogPageItemIds;
                }

                foreach (var catalogItemMetadata in catalogItems)
                {
                    ct.ThrowIfCancellationRequested();

                    if (processedItems >= maxItems)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(catalogItemMetadata.Id))
                    {
                        continue;
                    }

                    // catalog can contain multiple types
                    var (libraryFolder, baseItemKind) = GetLibraryFolder(catalogItemMetadata, cfg.SeriesFolder, cfg.MovieFolder);

                    if (libraryFolder is null)
                    {
                        continue;
                    }

                    try
                    {
                        var (item, isNewLibraryItem) = await manager.InsertMeta(
                            parent: libraryFolder,
                            meta: catalogItemMetadata,
                            user: null,
                            allowRemoteRefresh: true,
                            refreshItem: true,
                            queueRefreshItem: baseItemKind == BaseItemKind.Series,
                            ct).ConfigureAwait(false);

                        if (item is null)
                        {
                            continue;
                        }

                        allSeenLibraryCatalogItems.Add(item);

                        // This will make the method run until it imported the configured amount
                        // of items that were not present in the library yet, making it possible
                        // to gradually import the whole catalog.
                        //if (isNewLibraryItem)
                        //{
                        //    continue;
                        //}

                        processedItems++;
                        progress?.Report(processedItems * 100.0 / catalogCfg.MaxItems);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "{CatalogId}: insert meta failed for {MetadataId}",catalogId, catalogItemMetadata.Id);
                    }
                }

                skip += catalogItems.Count;
            }

            if (catalogCfg.CreateCollection)
            {
                await UpdateOrCreateCollectionAsync(cfg, catalogCfg, 100, allSeenLibraryCatalogItems.ToArray()).ConfigureAwait(false); // Hard cap of 100 items for a collection
            }

            logger.LogInformation("{CatalogName}: processed ({Count} items)", catalogName, processedItems);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Import for {CatalogName} aborted due to non-user cancellation!", catalogName);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Catalog sync failed for {CatalogName}: {Message}", catalogName, ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            progress?.Report(100);

            logger.LogInformation(
                "Catalog {catalog} sync completed in {Minutes}m {Seconds}s ({TotalSeconds:F2}s total)",
                catalogName,
                (int)stopwatch.Elapsed.TotalMinutes,
                stopwatch.Elapsed.Seconds,
                stopwatch.Elapsed.TotalSeconds);
        }
    }

    private (Folder?, BaseItemKind)  GetLibraryFolder(StremioMeta catalogItemMetadata, Folder? seriesFolder, Folder? moviesFolder)
    {
        var baseItemKind = catalogItemMetadata.Type.ToBaseItem();
        var libraryFolder = baseItemKind switch
        {
            BaseItemKind.Series => seriesFolder,
            BaseItemKind.Movie => moviesFolder,
            _ => null,
        };

        return (libraryFolder, baseItemKind);
    }

    private async Task<BoxSet?> GetOrCreateBoxSetAsync(CatalogConfig config)
    {
        var providerValue = $"{config.Type}.{config.Id}";

        var collection = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.BoxSet],
                    CollapseBoxSetItems = false,
                    Recursive = true,
                    HasAnyProviderId = new Dictionary<string, string> { { ProviderKey, providerValue } },
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
                        Name = $"{config.Name} ({config.Type.FirstCharToUpperInvariant()})",
                        IsLocked = true,
                        ProviderIds = new Dictionary<string, string> { { ProviderKey, providerValue } },
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

    private async Task UpdateOrCreateCollectionAsync(
        PluginConfiguration pluginConfig,
        CatalogConfig catalogConfig,
        int maxCollectionItemsAllowed,
        BaseItem[] allRetrievedCatalogItems)
    {
        var catalogName = $"{catalogConfig.Name} {catalogConfig.Type}";

        try
        {
            var collection = await GetOrCreateBoxSetAsync(catalogConfig).ConfigureAwait(false);
            if (collection is null)
            {
                logger.LogError("Unable to retrieve or create Jellyfin collection for {CatalogName}, catalog will be skipped.", catalogName);
                return;
            }

            var currentChildren = collection.GetLinkedChildren().Select(i => i.Id).ToArray();
            var currentChildrenSet = new HashSet<Guid>(currentChildren);

            var newItems = allRetrievedCatalogItems
                .Take(maxCollectionItemsAllowed)
                .Select(i => i.Id)
                .Where(id => !currentChildrenSet.Contains(id)).ToArray();

            if (newItems.Length == 0)
            {
                logger.LogInformation("No new items detected for {Name}.", catalogName);
                return;
            }

            var collectionCapReached = allRetrievedCatalogItems.Length > maxCollectionItemsAllowed;
            if (collectionCapReached)
            {
                logger.LogWarning("Max Collection Size reached for {CatalogName}, collection will be updated with the newest {MaxItems} items!", catalogName, maxCollectionItemsAllowed);
            }

            // We have to remove the current items, as we will be adding all the retrieved items in the order we got them from the catalog
            if (currentChildren.Length != 0)
            {
                await collectionManager.RemoveFromCollectionAsync(collection.Id, currentChildren).ConfigureAwait(false);
            }

            var amountToImport = collectionCapReached ? maxCollectionItemsAllowed : allRetrievedCatalogItems.Length;

            // Use this instead of above line if the check in the 'ImportCatalogAsync' method for 'isNewLibraryItem' is used:
            //var amountToImport = collectionCapReached ? maxCollectionItemsAllowed : Math.Max(allRetrievedCatalogItems.Length, catalogConfig.MaxItems);

            await collectionManager
                .AddToCollectionAsync(
                    collection.Id,
                    allRetrievedCatalogItems.Take(amountToImport).Select(i => i.Id))
                .ConfigureAwait(false);

            logger.LogInformation("Updated collection {CollectionName} with {Amount} new items. Total: {TotalItems}",
                catalogName, newItems.Length, amountToImport);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating collection for {CatalogName}", catalogName);
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
            var catalogName = $"{cat.Name} {cat.Type}";

            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Processing enabled catalog: {Name}", catalogName);

            var catMax = cat.MaxItems;
            var localOffset = offset;
            var catProgress = progress is null
                ? null
                : (IProgress<double>)new Progress<double>(p => progress.Report((localOffset + p / 100.0 * catMax) / total * 100.0));

            try
            {
                await ImportCatalogAsync(cat.Id, cat.Type, ct, catProgress).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "Import for {CatalogName} aborted, continuing with next catalog.", catalogName);
            }

            offset += catMax;
        }

        progress?.Report(100);
    }
}
