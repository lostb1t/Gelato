#nullable disable
#pragma warning disable CS1591

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators
{
    public sealed class SubtitleManagerDecorator : ISubtitleManager
    {
        private readonly ISubtitleManager _inner;
        private readonly ILogger<SubtitleManagerDecorator> _log;
        private readonly Lazy<ILibraryManager> _libraryManager;

        public SubtitleManagerDecorator(
            ISubtitleManager inner,
            ILogger<SubtitleManagerDecorator> log,
            Lazy<ILibraryManager> libraryManager
        )
        {
            _inner = inner;
            _log = log;
            _libraryManager = libraryManager;
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
        ) =>
            _inner.SearchSubtitles(video, language, isPerfectMatch, isAutomated, cancellationToken);

        public Task<RemoteSubtitleInfo[]> SearchSubtitles(
            SubtitleSearchRequest request,
            CancellationToken cancellationToken
        )
        {
            // nasty hack to prevent some plugins chocking on remote files
            // request.MediaPath = request.MediaPath + ".strm";
            return _inner.SearchSubtitles(request, cancellationToken);
        }

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
                // is a URL. Save to the item's metadata folder, the only place Gelato can find the
                // file again. Copy the options: GetLibraryOptions returns the instance Jellyfin
                // caches for the whole library.
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

        public Task UploadSubtitle(Video video, SubtitleResponse response) =>
            _inner.UploadSubtitle(video, response);

        public Task<SubtitleResponse> GetRemoteSubtitles(
            string id,
            CancellationToken cancellationToken
        ) => _inner.GetRemoteSubtitles(id, cancellationToken);

        public Task DeleteSubtitles(BaseItem item, int index) =>
            _inner.DeleteSubtitles(item, index);

        public SubtitleProviderInfo[] GetSupportedProviders(BaseItem item) =>
            _inner.GetSupportedProviders(item);
    }
}
