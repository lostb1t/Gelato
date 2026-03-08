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
    private static readonly string[] AdultKeywords =
    [
        "adult",
        "porn",
        "xxx",
        "hentai",
        "erotic",
        "nsfw",
        "18+",
        "sex",
    ];

    public async Task ImportCatalogAsync(
        string catalogId,
        string type,
        CancellationToken ct,
        IProgress<double>? progress = null,
        bool ignoreEnabled = false,
        bool importAllItems = false
    )
    {
        var catalogCfg = catalogService.GetCatalogConfig(catalogId, type);
        if (catalogCfg == null)
        {
            logger.LogWarning("Catalog config not found for {Id} {Type}", catalogId, type);
            return;
        }

        if (!ignoreEnabled && !catalogCfg.Enabled)
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

        var maxItems = importAllItems ? int.MaxValue : catalogCfg.MaxItems;
        long done = 0;

        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Starting import for catalog {Name} ({Id}) - Limit: {Limit} - ImportAllItems: {ImportAllItems}",
            catalogCfg.Name,
            catalogId,
            maxItems,
            importAllItems
        );

        try
        {
            var skip = 0;
            var processedItems = 0;
            var importedIds = new ConcurrentBag<Guid>();

            while (importAllItems || processedItems < maxItems)
            {
                ct.ThrowIfCancellationRequested();

                var page = await stremio
                    .GetCatalogMetasAsync(catalogId, type, search: null, skip: skip)
                    .ConfigureAwait(false);

                if (page.Count == 0)
                {
                    break;
                }

                foreach (var meta in page)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!importAllItems && processedItems >= maxItems)
                    {
                        break;
                    }

                    var mediaType = meta.Type;
                    var baseItemKind = mediaType.ToBaseItem();

                    // catalog can contain multiple types.

                    var root = ResolveImportRoot(
                        cfg,
                        catalogCfg,
                        meta,
                        baseItemKind,
                        movieFolder,
                        seriesFolder,
                        ct
                    );

                    if (root is not null)
                    {
                        try
                        {
                            var (item, _) = await manager
                                .InsertMeta(
                                    root,
                                    meta,
                                    null,
                                    true,
                                    true,
                                    baseItemKind == BaseItemKind.Series,
                                    ct
                                )
                                .ConfigureAwait(false);

                            if (item != null)
                                importedIds.Add(item.Id);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(
                                "{CatId}: insert meta failed for {Id}. Exception: {Message}\n{StackTrace}",
                                catalogId,
                                meta.Id,
                                ex.Message,
                                ex.StackTrace
                            );
                        }
                    }

                    processedItems++;
                    if (!importAllItems && maxItems > 0)
                        progress?.Report(processedItems * 100.0 / maxItems);
                }

                skip += page.Count;
            }

            if (catalogCfg.CreateCollection)
            {
                await UpdateCollectionAsync(catalogCfg, importedIds.Take(100).ToList())
                    .ConfigureAwait(false);
                importedIds.Clear();
            }

            logger.LogInformation("{Id}: processed ({Count} items)", catalogCfg.Id, processedItems);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                ex,
                "Catalog {Id} aborted due to non-user cancellation, continuing with next catalog",
                catalogId
            );
        }
        catch (Exception ex)
        {
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

    private Folder? ResolveImportRoot(
        PluginConfiguration cfg,
        CatalogConfig catalogCfg,
        StremioMeta meta,
        BaseItemKind kind,
        Folder? defaultMovieRoot,
        Folder? defaultSeriesRoot,
        CancellationToken ct
    )
    {
        var isAdult = IsAdult(meta, catalogCfg);

        var path = kind switch
        {
            BaseItemKind.Movie when isAdult && !string.IsNullOrWhiteSpace(cfg.AdultMoviePath) =>
                cfg.AdultMoviePath,
            BaseItemKind.Series when isAdult && !string.IsNullOrWhiteSpace(cfg.AdultSeriesPath) =>
                cfg.AdultSeriesPath,
            BaseItemKind.Movie => cfg.MoviePath,
            BaseItemKind.Series => cfg.SeriesPath,
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(path))
            return null;

        var baseRoot = kind switch
        {
            BaseItemKind.Movie when path == cfg.MoviePath => defaultMovieRoot,
            BaseItemKind.Series when path == cfg.SeriesPath => defaultSeriesRoot,
            _ => manager.TryGetFolderByPath(path),
        };

        if (baseRoot is null)
        {
            logger.LogWarning(
                "No root folder found for {Kind} path {Path}. Add this path to Jellyfin library and scan once.",
                kind,
                path
            );
            return null;
        }

        var bucket = isAdult ? "Adult" : GetPrimaryGenre(meta);
        if (!cfg.SplitCatalogImportsByGenre)
            return baseRoot;

        var genrePath = Path.Combine(path, SanitizeSegment(bucket));
        var genreFolder = manager.GetOrCreateSubFolder(baseRoot, genrePath, bucket, ct);
        return genreFolder ?? baseRoot;
    }

    private static string GetPrimaryGenre(StremioMeta meta)
    {
        var genres = meta.Genres ?? meta.Genre;
        if (genres is null || genres.Count == 0)
            return "Unknown";

        var first = genres.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
        return string.IsNullOrWhiteSpace(first) ? "Unknown" : first.Trim();
    }

    private static bool IsAdult(StremioMeta meta, CatalogConfig catalogCfg)
    {
        var genres = meta.Genres ?? meta.Genre ?? [];
        var joinedGenres = string.Join(' ', genres);
        var text = $"{meta.GetName()} {meta.Description} {meta.Overview} {catalogCfg.Name} {catalogCfg.Id} {joinedGenres}";

        return AdultKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(s) ? "Unknown" : s;
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

    private async Task UpdateCollectionAsync(CatalogConfig config, List<Guid> ids)
    {
        logger.LogInformation(
            "Updating collection {Name} with {Count} items",
            config.Name,
            ids.Count
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
            logger.LogInformation("Processing enabled catalog: {Name}", cat.Name);

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

    public async Task SyncAllUnfilteredAsync(CancellationToken ct)
    {
        var catalogs = await catalogService.GetCatalogsAsync(Guid.Empty);

        if (catalogs.Count == 0)
            return;

        foreach (var cat in catalogs)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Processing unfiltered catalog import: {Name}", cat.Name);
            await ImportCatalogAsync(
                    cat.Id,
                    cat.Type,
                    ct,
                    progress: null,
                    ignoreEnabled: true,
                    importAllItems: true
                )
                .ConfigureAwait(false);
        }

        libraryManager.QueueLibraryScan();
    }
}
