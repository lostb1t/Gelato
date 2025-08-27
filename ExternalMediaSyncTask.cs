#pragma warning disable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks
{
    // [Export(typeof(IScheduledTask))]
    public class ExternalMediaSyncTask : IScheduledTask
    {
        private const string ProviderKey = "Imdb";
        private const int BatchSize = 500; // tune as needed
        private const string ExtMoviesPUK = "ext:movies";
        private const string ExtShowsPUK = "ext:shows";

        /// <summary>
        /// Helper to resolve a Folder by path, compatible with different Jellyfin builds.
        /// </summary>
        private Folder? TryGetFolderByPath(string path)
        {
            // Newer builds expose FindByPath(string), older builds can be inconsistent.
            // First try FindByPath, then fall back to a repository query.
            var found = _library.FindByPath(path, true) as Folder;
            if (found != null)
                return found;

            var queried = _library.QueryItems(new InternalItemsQuery { Path = path })
                                  .Items
                                  .OfType<Folder>()
                                  .FirstOrDefault();
            return queried;
        }
        /// <summary>
        /// Ensure a stable virtual child folder under a collection. Creates it if missing.
        /// </summary>
        private Folder EnsureOrCreateVirtualChildFolder(CollectionFolder parentCollection, string displayName, string puk, string virtualPath)
        {
            // Compute a stable Id from the PresentationUniqueKey (PUK)
            var folderId = _library.GetNewItemId(puk, typeof(Folder));

            // Try to find existing by Id
            // if (_library.GetItemById(folderId) is Folder existing)
            // {
            //     return existing;
            // }

            // Create a new virtual folder under the collection
            var vf = new Folder
            {
                Id = folderId,
                Name = displayName,
                Path = virtualPath,
                //IsVirtualItem = true,
                ParentId = parentCollection.Id,
                DateCreated = DateTime.UtcNow,
                DateModified = DateTime.UtcNow,
                DateLastSaved = DateTime.UtcNow,
                PresentationUniqueKey = puk
            };

            _logger.LogInformation("Creating virtual folder '{Name}' under collection {CollectionName}", displayName, parentCollection.Name);
            _library.CreateItem(vf, parentCollection);
            parentCollection.AddChild(vf);


            return vf;
        }

        private readonly List<Series> _seriesBuffer = new(BatchSize);
        private readonly List<Season> _seasonBuffer = new(BatchSize);
        private readonly List<Episode> _episodeBuffer = new(BatchSize);

        private readonly IItemRepository _repo;
        private readonly ILibraryManager _library;
        private readonly ILogger<ExternalMediaSyncTask> _logger;
        private readonly IProviderManager _provider;

        // [ImportingConstructor]
        public ExternalMediaSyncTask(
            ILibraryManager library,
            ILogger<ExternalMediaSyncTask> logger,
            IProviderManager provider,
            IItemRepository repo)
        {
            _library = library;
            _logger = logger;
            _provider = provider;
            _repo = repo;
        }

        public string Name => "External Media: Sync catalog";
        public string Key => "ExternalMediaSync";
        public string Description => "Imports/updates items from IMDb basics into the Jellyfin database.";
        public string Category => "External Media";

        /// <inheritdoc/>
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("ExternalMediaSync: start");

            // Attach movies under an existing, physical folder inside your Movies library
            var movieParent = await EnsureMovieLib(cancellationToken).ConfigureAwait(false);
            var showParent = await EnsureShowLib(cancellationToken).ConfigureAwait(false);

            // Stream IMDb data to keep memory usage low and avoid accumulating lists
            var done = 0;
            var logged = 0;
            var moviesBuffer = new List<Movie>(BatchSize);
            _seriesBuffer.Clear();
            _seasonBuffer.Clear();
            _episodeBuffer.Clear();

            await foreach (var t in Imdb.StreamImdbAsync(
                                    cacheDir: "/tmp/imdb-cache",
                                    includeEpisodeLinkage: true,
                                    ct: cancellationToken)
                              .WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (t.TitleType)
                {
                    case ImdbTitleType.TvSeries:
                        if (IntoSeries(showParent, t, cancellationToken) is { } s)
                        {
                            _seriesBuffer.Add(s);
                            if (_seriesBuffer.Count >= BatchSize)
                            {
                                _library.CreateItems(_seriesBuffer, showParent, cancellationToken);
                                done += _seriesBuffer.Count;
                                _seriesBuffer.Clear();
                            }
                        }
                        break;

                    case ImdbTitleType.TvEpisode:
                        if (IntoSeason(t, cancellationToken) is { } season)
                        {
                            _seasonBuffer.Add(season);
                            if (_seasonBuffer.Count >= BatchSize)
                            {
                                _library.CreateItems(_seasonBuffer, showParent, cancellationToken);
                                done += _seasonBuffer.Count;
                                _seasonBuffer.Clear();
                            }
                        }
                        if (IntoEpisode(t, cancellationToken) is { } ep)
                        {
                            _episodeBuffer.Add(ep);
                            if (_episodeBuffer.Count >= BatchSize)
                            {
                                _library.CreateItems(_episodeBuffer, showParent, cancellationToken);
                                done += _episodeBuffer.Count;
                                _episodeBuffer.Clear();
                            }
                        }
                        break;

                    case ImdbTitleType.Movie:
                        if (IntoMovie(movieParent, t, cancellationToken) is { } m)
                        {
                            moviesBuffer.Add(m);
                            if (moviesBuffer.Count >= BatchSize)
                            {
                                _library.CreateItems(moviesBuffer, movieParent, cancellationToken);
                                done += moviesBuffer.Count;
                                foreach (var n in moviesBuffer)
                                {
                                    _repo.SaveImages(n);
                                }
                                moviesBuffer.Clear();
                            }
                        }
                        break;
                }

                if (done - logged >= 5000)
                {
                    logged = done;
                    _logger.LogInformation("ExternalMediaSync: streamed ~{Count} items so far...", done);
                    // Update progress based on estimated total
                    var pct = (double)done / 1_500_000 * 100.0;
                    progress?.Report(Math.Min(100.0, pct));

                }
            }

            // flush tails
            if (_seriesBuffer.Count > 0) { _library.CreateItems(_seriesBuffer, showParent, cancellationToken); done += _seriesBuffer.Count; _seriesBuffer.Clear(); }
            if (_seasonBuffer.Count > 0) { _library.CreateItems(_seasonBuffer, showParent, cancellationToken); done += _seasonBuffer.Count; _seasonBuffer.Clear(); }
            if (_episodeBuffer.Count > 0) { _library.CreateItems(_episodeBuffer, showParent, cancellationToken); done += _episodeBuffer.Count; _episodeBuffer.Clear(); }
            if (moviesBuffer.Count > 0) { _library.CreateItems(moviesBuffer, movieParent, cancellationToken); done += moviesBuffer.Count; moviesBuffer.Clear(); }

            // NOTE: consider disabling ValidateMediaLibrary if this inflates counts unexpectedly
            _logger.LogInformation("ExternalMediaSync: validating library");
            await _library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("ExternalMediaSync: done");
            progress?.Report(100.0);
        }

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // No automatic schedule; run manually
            yield break;
        }

        /// <summary>
        /// Seeds a folder with a small placeholder file if it is empty. Needed for jellyfin to "work"
        /// </summary>
        /// <param name="path"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private static async Task SeedFolderAsync(string path, CancellationToken ct)
        {
            Directory.CreateDirectory(path);
            var seed = System.IO.Path.Combine(path, "stub.txt");
            if (!File.Exists(seed))
                await File.WriteAllBytesAsync(seed, Array.Empty<byte>(), ct);
        }

        /// <summary>
        /// Ensures a library exists for the given path and type (creating it if missing) and returns the physical folder item.
        /// </summary>
        private async Task<Folder> EnsureLibAsync(string libPath, string libName, CollectionTypeOptions collectionType, CancellationToken ct)
        {
            Directory.CreateDirectory(libPath);
            await SeedFolderAsync(libPath, ct);

            // 1) Does any collection already include this path?
            var collections = _library.QueryItems(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.CollectionFolder },
                Recursive = false,
            }).Items.OfType<CollectionFolder>().ToList();

            foreach (var cf in collections)
            {
                _logger.LogInformation("Collection: {Name} ({Type})", cf.Name, cf.CollectionType);
            }

            var hasPath = collections.Any(cf => (cf.PhysicalLocationsList?.Any(p => string.Equals(p, libPath, StringComparison.OrdinalIgnoreCase)) ?? false));

            // 2) If no collection with this path, create one we control
            if (!hasPath)
            {
                _logger.LogInformation("No collection includes {Path}. Creating virtual folder '{Name}'.", libPath, libName);
                var options = new LibraryOptions
                {
                    EnableRealtimeMonitor = false,
                    SaveLocalMetadata = false,
                    EnableInternetProviders = false,
                    TypeOptions = new[] {
                        new TypeOptions
                        {
                            MetadataFetchers = Array.Empty<string>(),
                            ImageFetchers = Array.Empty<string>()
                        }
                    },
                    PathInfos = new[] { new MediaPathInfo { Path = libPath } }
                };

                await _library.AddVirtualFolder(libName, collectionType, options, refreshLibrary: true)
                    .ConfigureAwait(false);
            }

            // 3) Validate/scan so the physical Folder item is materialized
            _logger.LogInformation("Validating media library to materialize folder for {Path}", libPath);
            await _library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);

            // 4) Poll for up to 60s until the physical folder item exists
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < TimeSpan.FromSeconds(60))
            {
                if (TryGetFolderByPath(libPath) is Folder materialized)
                {
                    _logger.LogInformation("Resolved parent folder for {Path}: {Name} ({Id})", libPath, materialized.Name, materialized.Id);
                    return materialized;
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"Physical folder item not found for path '{libPath}' after creating/validating library.");
        }

        /// <summary>
        /// Ensures a Movies collection exists (creating it if missing) and returns the physical folder item.
        /// </summary>
        private async Task<Folder> EnsureMovieLib(CancellationToken ct)
        {
            return await EnsureLibAsync("/media/movies", "External Movies", CollectionTypeOptions.movies, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Ensures a TV Shows collection exists (creating it if missing) and returns the physical folder item.
        /// </summary>
        private async Task<Folder> EnsureShowLib(CancellationToken ct)
        {
            return await EnsureLibAsync("/media/shows", "External Shows", CollectionTypeOptions.tvshows, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Create or skip a Movie for the given IMDb basic title (movie only). Returns the Movie for batch insert, or null if skipped.
        /// </summary>
        private Movie? IntoMovie(Folder parent, ImdbBasicTitle t, CancellationToken ct)
        {
            // _logger.LogInformation("builder");
            // Guard: only movies here
            if (t.TitleType != ImdbTitleType.Movie)
            {
                _logger.LogDebug("Skipping non-movie title {Tconst} ({Type})", t.Tconst, t.TitleType);
                return null;
            }

            var movie = new Movie
            {
                Id = _library.GetNewItemId(t.Tconst, typeof(Movie)),
                Name = string.IsNullOrWhiteSpace(t.PrimaryTitle) ? t.Tconst : t.PrimaryTitle,
                //Path = $"stremio://movie/{t.Tconst}",
                IsVirtualItem = true,
                // Container = "mp4",
                ParentId = parent.Id,
                DateCreated = DateTime.UtcNow,
                DateModified = DateTime.UtcNow,
                DateLastSaved = DateTime.UtcNow
            };

            // Map basic fields
            if (t.StartYear.HasValue)
            {
                movie.ProductionYear = t.StartYear;
            }

            if (t.RuntimeMinutes.HasValue && t.RuntimeMinutes.Value > 0)
            {
                movie.RunTimeTicks = TimeSpan.FromMinutes(t.RuntimeMinutes.Value).Ticks;
            }

            if (t.Genres?.Length > 0)
            {
                movie.Genres = t.Genres;
            }

            // Provider ids & presentation key
            movie.SetProviderId(MetadataProvider.Imdb, t.Tconst);
            movie.PresentationUniqueKey = movie.CreatePresentationUniqueKey();
            AttachExternalImages(movie, t.Tconst);
            _logger.LogDebug("Inserted movie: {Title} ({Imdb})", movie.Name, t.Tconst);
            return movie;
        }

        // private void DumpItems(object obj)
        // {
        //     foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(obj))
        //     {
        //         var name = descriptor.Name;
        //         var value = descriptor.GetValue(obj);
        //         Console.WriteLine("{0}={1}", name, value);
        //     }
        // }

        private Series? IntoSeries(Folder parent, ImdbBasicTitle t, CancellationToken ct)
        {
            if (t.TitleType != ImdbTitleType.TvSeries)
            {
                return null;
            }

            var series = new Series
            {
                Id = _library.GetNewItemId(t.Tconst, typeof(Series)),
                Name = string.IsNullOrWhiteSpace(t.PrimaryTitle) ? t.Tconst : t.PrimaryTitle,
                Path = $"stremio://series/{t.Tconst}",
                IsVirtualItem = true,
                ParentId = parent.Id,
                DateCreated = DateTime.UtcNow,
                DateModified = DateTime.UtcNow,
                DateLastSaved = DateTime.UtcNow
            };

            if (t.StartYear.HasValue)
            {
                series.ProductionYear = t.StartYear;
            }

            if (t.Genres?.Length > 0)
            {
                series.Genres = t.Genres;
            }

            series.SetProviderId(MetadataProvider.Imdb, t.Tconst);
            series.PresentationUniqueKey = series.CreatePresentationUniqueKey();

            _logger.LogDebug("Prepared series: {Title} ({Imdb})", series.Name, t.Tconst);
            return series;
        }

        private Season? IntoSeason(ImdbBasicTitle episodeTitle, CancellationToken ct)
        {
            if (episodeTitle.TitleType != ImdbTitleType.TvEpisode)
            {
                return null;
            }

            if (!episodeTitle.SeasonNumber.HasValue || string.IsNullOrEmpty(episodeTitle.ParentTconst))
            {
                return null;
            }

            var seasonNum = episodeTitle.SeasonNumber.Value;
            var seriesTconst = episodeTitle.ParentTconst!;

            var season = new Season
            {
                Id = _library.GetNewItemId($"{seriesTconst}:season:{seasonNum}", typeof(Season)),
                Name = $"Season {seasonNum}",
                IndexNumber = seasonNum,
                Path = $"stremio://series/{seriesTconst}:{seasonNum}",
                IsVirtualItem = true,
                ParentId = _library.GetNewItemId(seriesTconst, typeof(Series)),
                DateCreated = DateTime.UtcNow,
                DateModified = DateTime.UtcNow,
                DateLastSaved = DateTime.UtcNow
            };

            season.SetProviderId(MetadataProvider.Imdb, $"{seriesTconst}:S{seasonNum}");
            season.PresentationUniqueKey = season.CreatePresentationUniqueKey();

            _logger.LogDebug("Prepared season {Season} for series {Series}", seasonNum, seriesTconst);
            return season;
        }

        private Episode? IntoEpisode(ImdbBasicTitle t, CancellationToken ct)
        {
            if (t.TitleType != ImdbTitleType.TvEpisode)
            {
                return null;
            }

            if (!t.SeasonNumber.HasValue || !t.EpisodeNumber.HasValue || string.IsNullOrEmpty(t.ParentTconst))
            {
                return null;
            }

            var seriesTconst = t.ParentTconst!;
            var seasonNum = t.SeasonNumber.Value;
            var epNum = t.EpisodeNumber.Value;

            var ep = new Episode
            {
                Id = _library.GetNewItemId(t.Tconst, typeof(Episode)),
                Name = string.IsNullOrWhiteSpace(t.PrimaryTitle) ? $"E{epNum}" : t.PrimaryTitle,
                IndexNumber = epNum,
                ParentIndexNumber = seasonNum,
                Path = $"stremio://series/{seriesTconst}:{seasonNum}:{epNum}",
                IsVirtualItem = true,
                ParentId = _library.GetNewItemId($"{seriesTconst}:season:{seasonNum}", typeof(Season)),
                DateCreated = DateTime.UtcNow,
                DateModified = DateTime.UtcNow,
                DateLastSaved = DateTime.UtcNow
            };

            if (t.RuntimeMinutes.HasValue && t.RuntimeMinutes.Value > 0)
            {
                ep.RunTimeTicks = TimeSpan.FromMinutes(t.RuntimeMinutes.Value).Ticks;
            }

            if (t.Genres?.Length > 0)
            {
                ep.Genres = t.Genres;
            }

            ep.SetProviderId(MetadataProvider.Imdb, t.Tconst);
            ep.PresentationUniqueKey = ep.CreatePresentationUniqueKey();

            _logger.LogDebug("Prepared episode: S{Season}E{Episode} ({Imdb})", seasonNum, epNum, t.Tconst);
            return ep;
        }



        private static string BuildStremioId(string imdbId, int? season = null, int? episode = null)
        {
            if (season.HasValue)
                return $"{imdbId}:{season.Value}:{(episode ?? 0)}";
            return imdbId;
        }

        private static string BuildImageUrl(string id, ImageType type)
        {
            return $"http://localhost:8096/externalmedia/image/{Uri.EscapeDataString(id)}/{type}";
        }

        private static void AttachExternalImages(BaseItem item, string imdbId, int? season = null, int? episode = null)
        {
            var id = BuildStremioId(imdbId, season, episode);

            var images = new List<ItemImageInfo>
    {
        new ItemImageInfo { Path = BuildImageUrl(id, ImageType.Primary),  Type = ImageType.Primary  },
        new ItemImageInfo { Path = BuildImageUrl(id, ImageType.Logo),     Type = ImageType.Logo     },
        new ItemImageInfo { Path = BuildImageUrl(id, ImageType.Backdrop), Type = ImageType.Backdrop }
    };

            // Preserve any existing images and append ours
            var existing = item.ImageInfos?.ToList() ?? new List<ItemImageInfo>();
            existing.AddRange(images);
            item.ImageInfos = existing.ToArray();
        }
    }

}
