#nullable disable
#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators
{
    public sealed class SubtitleManagerDecorator : ISubtitleManager
    {
        private readonly ISubtitleManager _inner;
        private readonly ILogger<SubtitleManagerDecorator> _log;
        private readonly Lazy<ILibraryManager> _libraryManager;
        private readonly ILocalizationManager _localization;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IUserManager _userManager;
        private readonly IHttpContextAccessor _http;

        public SubtitleManagerDecorator(
            ISubtitleManager inner,
            ILogger<SubtitleManagerDecorator> log,
            Lazy<ILibraryManager> libraryManager,
            ILocalizationManager localization,
            IMediaSourceManager mediaSourceManager,
            IUserManager userManager,
            IHttpContextAccessor http
        )
        {
            _inner = inner;
            _log = log;
            _libraryManager = libraryManager;
            _localization = localization;
            _mediaSourceManager = mediaSourceManager;
            _userManager = userManager;
            _http = http;
        }

        public event EventHandler<SubtitleDownloadFailureEventArgs> SubtitleDownloadFailure
        {
            add => _inner.SubtitleDownloadFailure += value;
            remove => _inner.SubtitleDownloadFailure -= value;
        }

        public Task<RemoteSubtitleInfo[]> SearchSubtitles(
            Video video,
            string language,
            bool? isPerfectMatch,
            bool isAutomated,
            CancellationToken cancellationToken
        )
        {
            // Jellyfin builds the request inside its own implementation of this overload, so the
            // guard cannot sit in the request overload alone.
            if (isAutomated && SkipAutomatedSearch(video.Path, language, video))
                return Task.FromResult(Array.Empty<RemoteSubtitleInfo>());

            // The subtitle dialog on a movie/episode's page only knows the item, and its path is
            // the placeholder: the results were ranked against "tt0349047". Rank them against the
            // release that page plays. Jellyfin builds the request from the item it is handed,
            // and the row is the same movie/episode with the release's path and file name.
            if (!isAutomated && PlayingRow(video) is { } row)
                video = row;

            return _inner.SearchSubtitles(
                video,
                language,
                isPerfectMatch,
                isAutomated,
                cancellationToken
            );
        }

        public Task<RemoteSubtitleInfo[]> SearchSubtitles(
            SubtitleSearchRequest request,
            CancellationToken cancellationToken
        )
        {
            // nasty hack to prevent some plugins chocking on remote files
            // request.MediaPath = request.MediaPath + ".strm";
            if (request.IsAutomated && SkipAutomatedSearch(request.MediaPath, request.Language))
                return Task.FromResult(Array.Empty<RemoteSubtitleInfo>());

            return _inner.SearchSubtitles(request, cancellationToken);
        }

        /// <summary>
        /// The "Download missing subtitles" task picks every video without an external subtitle
        /// stream in the database. Jellyfin only records those for local files, so every Gelato item
        /// looks like it is missing one, and the task fetched the same subtitle again on each run.
        /// SubtitleManager never overwrites, it saves the next copy as .en.0.vtt, .en.1.vtt, …
        /// </summary>
        /// <param name="path">The item's path, as the caller sees it.</param>
        /// <param name="language">The language the search asks for.</param>
        /// <param name="item">The item when the caller has it, looked up by path otherwise.</param>
        private bool SkipAutomatedSearch(string path, string language, BaseItem item = null)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            // Placeholders (gelato://stub/…) are not a release, and what is saved for one is
            // offered with every version: only a subtitle picked by hand belongs there.
            if (path.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogDebug("Skipping automated subtitle search for placeholder {Path}", path);
                return true;
            }

            // Stream items. Probing runs under a local /tmp/<release>.strm path, where Jellyfin
            // finds the saved files itself.
            if (!path.IsUrl())
                return false;

            // Stream rows are alternate versions, which Jellyfin leaves out of queries by default.
            item ??= _libraryManager
                .Value.GetItemList(new InternalItemsQuery { Path = path, IncludeOwnedItems = true })
                .FirstOrDefault();
            if (item is null || !item.IsGelato())
                return false;

            var wanted = NormalizeLanguage(language);
            if (
                item.GetGelatoSubtitleFiles()
                    .Any(f =>
                        string.Equals(
                            NormalizeLanguage(f.Language),
                            wanted,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
            )
            {
                _log.LogDebug(
                    "Skipping automated subtitle search for {Id}, a {Language} subtitle is already saved",
                    item.Id,
                    language
                );
                return true;
            }

            return false;
        }

        // Library options use three-letter codes (eng), saved files the addon's code (en).
        private string NormalizeLanguage(string language) =>
            string.IsNullOrEmpty(language)
                ? language
                : _localization.FindLanguageInfo(language)?.TwoLetterISOLanguageName ?? language;

        public Task DownloadSubtitles(
            Video video,
            string subtitleId,
            CancellationToken cancellationToken
        )
        {
            // This is the overload Jellyfin calls (the scheduled task, metadata refresh and the
            // subtitle API). The inner one loads the library options and saves on its own, so
            // route Gelato items through the overload below.
            if (video.IsGelato())
            {
                return DownloadSubtitles(
                    video,
                    _libraryManager.Value.GetLibraryOptions(video),
                    subtitleId,
                    cancellationToken
                );
            }

            return _inner.DownloadSubtitles(video, subtitleId, cancellationToken);
        }

        public async Task DownloadSubtitles(
            Video video,
            LibraryOptions libraryOptions,
            string subtitleId,
            CancellationToken cancellationToken
        )
        {
            if (video.IsGelato())
            {
                // A Gelato item has no folder to save next to. With the name swap below the "media
                // folder" would be the server's working directory, and for a placeholder the path
                // is a URL. Save to the item's metadata folder, which is where
                // GetGelatoSubtitleFiles looks. Copy the options: GetLibraryOptions returns the
                // instance Jellyfin caches for the whole library.
                if (libraryOptions.SaveSubtitlesWithMedia)
                {
                    libraryOptions = JsonSerializer.Deserialize<LibraryOptions>(
                        JsonSerializer.Serialize(libraryOptions)
                    );
                    libraryOptions.SaveSubtitlesWithMedia = false;
                }

                // Jellyfin derives the file name from video.Path, which here is a URL or a
                // gelato://stub path and would produce a name nothing looks for afterwards.
                var originalPath = video.Path;
                video.Path = video.GelatoSubtitlePathName();
                try
                {
                    await _inner
                        .DownloadSubtitles(video, libraryOptions, subtitleId, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    video.Path = originalPath;
                }
                return;
            }

            await _inner
                .DownloadSubtitles(video, libraryOptions, subtitleId, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task UploadSubtitle(Video video, SubtitleResponse response)
        {
            if (!video.IsGelatoPlaybackItem())
            {
                await _inner.UploadSubtitle(video, response).ConfigureAwait(false);
                return;
            }

            // As in a download, the file has to get the name GetGelatoSubtitleFiles looks for.
            // Jellyfin reads the library options itself here, so the folder cannot be chosen
            // through them: a path inside the item's metadata folder makes that folder the
            // "media folder" as well.
            var originalPath = video.Path;
            video.Path = Path.Combine(
                video.GetInternalMetadataPath(),
                video.GelatoSubtitlePathName()
            );
            try
            {
                await _inner.UploadSubtitle(video, response).ConfigureAwait(false);
            }
            finally
            {
                video.Path = originalPath;
            }
        }

        public Task<SubtitleResponse> GetRemoteSubtitles(
            string id,
            CancellationToken cancellationToken
        ) => _inner.GetRemoteSubtitles(id, cancellationToken);

        public Task DeleteSubtitles(BaseItem item, int index)
        {
            // Jellyfin looks the stream up in the database under the item's id. The files Gelato
            // lists from the metadata folders are not in it, and a movie/episode is listed with
            // the streams of the row it plays.
            if (item.IsGelatoPlaybackItem() && ListedSubtitleFile(item, index) is { } path)
            {
                _log.LogInformation("Deleting subtitle {Path}", path);
                File.Delete(path);
                return Task.CompletedTask;
            }

            return _inner.DeleteSubtitles(item, index);
        }

        /// <summary>
        /// The source a movie/episode's page plays for the user of the request: the first of its
        /// sources, the resumed version before the others. The item itself is listed with that
        /// source's streams, in the subtitle dialog too.
        /// </summary>
        private MediaSourceInfo PlayingSource(BaseItem item)
        {
            var user = _http.ReadRequest(
                ctx => ctx.TryGetUserId(out var id) ? _userManager.GetUserById(id) : null,
                null
            );
            return _mediaSourceManager.GetStaticMediaSources(item, false, user).FirstOrDefault();
        }

        /// <summary>
        /// The stream row behind <see cref="PlayingSource"/> of a placeholder, when it has one.
        /// </summary>
        private Video PlayingRow(Video video)
        {
            if (video.HasStreamTag() || !video.IsGelatoPlaybackItem())
                return null;

            // The first stream is listed under the movie's id and names its row in the ETag.
            var row = Guid.TryParse(PlayingSource(video)?.ETag, out var rowId)
                ? _libraryManager.Value.GetItemById(rowId) as Video
                : null;
            return row is not null && row.HasStreamTag() ? row : null;
        }

        /// <summary>
        /// The file of the external subtitle an item is listed with under an index, when Gelato
        /// saved it: for the row that plays or for its movie/episode.
        /// </summary>
        private string ListedSubtitleFile(BaseItem item, int index)
        {
            var source = PlayingSource(item);
            var path = source
                ?.MediaStreams?.FirstOrDefault(s =>
                    s.Type == MediaStreamType.Subtitle && s.IsExternal && s.Index == index
                )
                ?.Path;
            if (string.IsNullOrEmpty(path))
                return null;

            var library = _libraryManager.Value;
            var row = Guid.TryParse(source.ETag, out var rowId) ? library.GetItemById(rowId) : null;
            BaseItem[] savedFor = [item.PrimaryVersionOrSelf(library), row ?? item];
            return savedFor.Any(owner => owner.GetGelatoSubtitleFiles().Any(f => f.Path == path))
                ? path
                : null;
        }

        public SubtitleProviderInfo[] GetSupportedProviders(BaseItem item) =>
            _inner.GetSupportedProviders(item);
    }
}
