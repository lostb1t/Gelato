#nullable disable
#pragma warning disable CS1591

using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Providers;

namespace Gelato.Decorators
{
    public sealed class SubtitleManagerDecorator : ISubtitleManager
    {
        private readonly ISubtitleManager _inner;

        public SubtitleManagerDecorator(ISubtitleManager inner)
        {
            _inner = inner;
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

        public async Task DownloadSubtitles(
            Video video,
            string subtitleId,
            CancellationToken cancellationToken
        )
        {
            var gelatoFilename = video.IsGelato() ? video.GelatoData<string>("filename") : null;
            if (!string.IsNullOrEmpty(gelatoFilename))
            {
                var originalPath = video.Path;
                video.Path = "/gelato/" + gelatoFilename;
                try
                {
                    await _inner
                        .DownloadSubtitles(video, subtitleId, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    video.Path = originalPath;
                }
                return;
            }

            await _inner
                .DownloadSubtitles(video, subtitleId, cancellationToken)
                .ConfigureAwait(false);
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
                libraryOptions.SaveSubtitlesWithMedia = false;

                // Jellyfin derives the subtitle save filename from video.Path.
                // For gelato stream items the path is a URL, which produces garbage names.
                // Use the BehaviorHints.Filename stored in GelatoData if available.
                var gelatoFilename = video.GelatoData<string>("filename");
                if (!string.IsNullOrEmpty(gelatoFilename))
                {
                    var originalPath = video.Path;
                    video.Path = "/gelato/" + gelatoFilename;
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
