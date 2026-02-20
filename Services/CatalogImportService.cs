using Gelato.Configuration;
using Gelato.Services;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums; // Potentially needed for BaseItemKind
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities.Movies; // For BoxSet

namespace Gelato.Services;

public class CatalogImportService {
    private readonly ILogger<CatalogImportService> _logger;
    private readonly GelatoManager _manager;
    private readonly GelatoStremioProviderFactory _stremioFactory;
    private readonly CatalogService _catalogService;
    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryManager _libraryManager;

    public CatalogImportService(
        ILogger<CatalogImportService> logger,
        GelatoManager manager,
        GelatoStremioProviderFactory stremioFactory,
        CatalogService catalogService,
        ICollectionManager collectionManager,
        ILibraryManager libraryManager
    ) {
        _logger = logger;
        _manager = manager;
        _stremioFactory = stremioFactory;
        _catalogService = catalogService;
        _collectionManager = collectionManager;
        _libraryManager = libraryManager;
    }

    public async Task ImportCatalogAsync(string catalogId, string type, Guid userId, CancellationToken ct, IProgress<double>? progress = null) {
        var config = _catalogService.GetCatalogConfig(catalogId, type);
        if (config == null) {
            _logger.LogWarning("Catalog config not found for {Id} {Type}", catalogId, type);
            return;
        }

        if (!config.Enabled) {
             _logger.LogInformation("Catalog {Id} {Type} is disabled, skipping.", catalogId, type);
             return;
        }

        var provider = _stremioFactory.Create(userId);
        var isSeries = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase);
        
        // Determine root folder
        var rootFolder = isSeries 
            ? _manager.TryGetSeriesFolder(userId) 
            : _manager.TryGetMovieFolder(userId);

        if (rootFolder == null) {
            _logger.LogError("Root folder not configured for {Type}", type);
            return;
        }

        var globalMaxItems = GelatoPlugin.Instance!.Configuration.CatalogMaxItems;
        var maxItems = config.MaxItems > 0 ? config.MaxItems : globalMaxItems;

        _logger.LogInformation("Starting import for catalog {Name} ({Id}) - Limit: {Limit}", config.Name, catalogId, maxItems);

        var items = new List<StremioMeta>();
        int skip = 0;
        while (items.Count < maxItems) {
            ct.ThrowIfCancellationRequested();
            
            // Fetch next page
            var batch = await provider.GetCatalogMetasAsync(catalogId, type, skip: skip).ConfigureAwait(false);
            if (batch == null || batch.Count == 0) break;

            foreach (var meta in batch) {
                if (items.Count >= maxItems) break;
                items.Add(meta);
            }
            
            skip += batch.Count;
            // Heuristic for end of catalog if page is small (assuming default page size usually >= 20 or similar)
            if (batch.Count < 20) break; 
        }

        _logger.LogInformation("Fetched {Count} items for catalog {Name}", items.Count, config.Name);

        // Process items
        int processed = 0;
        int total = items.Count;
        var importedIds = new ConcurrentBag<Guid>();

        if (isSeries) {
            // Sequential processing for Series to avoid rate limits on deep metadata fetching
            foreach (var meta in items) {
                ct.ThrowIfCancellationRequested();
                try {
                    var (item, _) = await _manager.InsertMeta(
                        rootFolder, 
                        meta, 
                        null, 
                        allowRemoteRefresh: true, 
                        refreshItem: true, 
                        queueRefreshItem: false, 
                        ct: ct
                    ).ConfigureAwait(false);
                    
                    if (item != null) importedIds.Add(item.Id);

                } catch (Exception ex) {
                    _logger.LogError(ex, "Error importing series {Name}", meta.Name);
                }
                
                processed++;
                progress?.Report((double)processed / total * 100);
            }
        } else {
             // Parallel processing for Movies
             var parallelOptions = new ParallelOptions {
                 MaxDegreeOfParallelism = 5,
                 CancellationToken = ct
             };

             await Parallel.ForEachAsync(items, parallelOptions, async (meta, token) => {
                 try {
                     var (item, _) = await _manager.InsertMeta(
                         rootFolder, 
                         meta, 
                         null, 
                         allowRemoteRefresh: true, 
                         refreshItem: true, 
                         queueRefreshItem: false, 
                         token
                     ).ConfigureAwait(false);
                     
                     if (item != null) importedIds.Add(item.Id);

                 } catch (Exception ex) {
                     _logger.LogError(ex, "Error importing movie {Name}", meta.Name);
                 }
                 
                 Interlocked.Increment(ref processed);
                 progress?.Report((double)processed / total * 100);
             }).ConfigureAwait(false);
        }

        // Handle Collection (per-catalog setting)
        if (config.CreateCollection && importedIds.Any()) {
            await UpdateCollectionAsync(config, importedIds.ToList()).ConfigureAwait(false);
        }

        _logger.LogInformation("Finished importing catalog {Name}", config.Name);
    }

    private async Task<BoxSet?> GetOrCreateBoxSetAsync(CatalogConfig config) {
        var id = $"{config.Type}.{config.Id}";
        var collection = _libraryManager.GetItemList(new InternalItemsQuery {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            CollapseBoxSetItems = false,
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string> { { "Stremio", id } }
        }).OfType<BoxSet>().FirstOrDefault();

        if (collection is null) {
            collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions {
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
                // We supplement the collection, or replace? 
                // Existing logic seemed to replace children?
                // "RemoveFromCollectionAsync(collection.Id, childrenIds)"
                // Yes, it replaces the content to match the catalog.
                
                var currentChildren = _libraryManager.GetItemList(new InternalItemsQuery {
                    Parent = collection,
                    Recursive = false
                }).Select(i => i.Id).ToList();

                // To match "Straight approach" and behave like a catalog sync:
                // We should probably ensure the collection reflects the catalog.
                // But we only imported 'MaxItems'.
                // If we remove everything else, we might lose items if MaxItems < Total.
                // But usually catalogs are "Top 100" etc.
                // I'll follow the replacement logic but maybe only remove what's NOT in the new list IF we assume we fetched everything intended.
                // The previous logic removed ALL children and added new ones. I'll stick to that.
                
                if (currentChildren.Any()) {
                    await _collectionManager.RemoveFromCollectionAsync(collection.Id, currentChildren).ConfigureAwait(false);
                }
                
                var itemsToAdd = ids.ToList();

                await _collectionManager.AddToCollectionAsync(collection.Id, itemsToAdd).ConfigureAwait(false);
                _logger.LogInformation("Updated collection {Name} with {Count} items", config.Name, itemsToAdd.Count);
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "Error updating collection for {Name}", config.Name);
        }
    }

    public async Task SyncAllEnabledAsync(CancellationToken ct, IProgress<double>? progress = null) {
         // Force refresh of catalog list from manifest first
         var catalogs = await _catalogService.GetCatalogsAsync(Guid.Empty);
         var enabled = catalogs.Where(c => c.Enabled).ToList();

         int current = 0;
         int total = enabled.Count;

         foreach (var cat in enabled) {
             ct.ThrowIfCancellationRequested();
             _logger.LogInformation("Processing enabled catalog: {Name}", cat.Name);
             
             // Create specific progress reporter for this catalog if needed, or just log
             await ImportCatalogAsync(cat.Id, cat.Type, Guid.Empty, ct).ConfigureAwait(false);
             
             current++;
             progress?.Report((double)current / total * 100);
         }
         await _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
    }
}
