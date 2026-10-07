using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.RemuxDb;

/// <summary>
/// Gives stream rows their tracks, runtime and size from RemuxDB when they are synced, and sends
/// what playback probed back to it.
/// </summary>
/// <remarks>
/// A row records where its media info came from in its Gelato data (<c>mediaInfo</c>):
/// <c>remuxdb</c> or <c>probe</c>. A probe of the file itself is never replaced by RemuxDB's.
/// </remarks>
public sealed class RemuxDbService(
    RemuxDbClient client,
    RemuxDbContributor contributor,
    IMediaStreamRepository mediaStreams,
    IChapterRepository chapters,
    ILibraryManager libraryManager,
    ILogger<RemuxDbService> log
)
{
    public const string SourceRemuxDb = "remuxdb";
    public const string SourceProbe = "probe";

    private static bool Enabled => GelatoPlugin.Instance?.Configuration.RemuxDbEnabled ?? false;

    private static bool Contribute =>
        GelatoPlugin.Instance?.Configuration.RemuxDbContribute ?? false;

    /// <summary>
    /// The versions RemuxDB knows for a movie or episode, or none when it is turned off or the
    /// title has no IMDb id.
    /// </summary>
    public Task<IReadOnlyList<RemuxDbVersion>> LookupAsync(string? stremioId, CancellationToken ct)
    {
        var title = RemuxDbTitle.FromStremioId(stremioId);
        if (!Enabled || title is null)
            return Task.FromResult<IReadOnlyList<RemuxDbVersion>>([]);

        return client.GetVersionsAsync(title, ct);
    }

    /// <summary>
    /// Starts <see cref="LookupAsync"/> for a title whose stream sync is about to follow.
    /// </summary>
    public void LookupAhead(string? stremioId)
    {
        if (Enabled && RemuxDbTitle.FromStremioId(stremioId) is { } title)
            client.StartLookupAhead(title);
    }

    /// <summary>
    /// Records the stream's file on the row and, when RemuxDB knows the file, sets the row's
    /// runtime, size, container and bitrate. The tracks are returned to be saved with
    /// <see cref="Save"/> once the row is in the database.
    /// </summary>
    public PendingMediaInfo? Apply(
        Video row,
        bool isNewRow,
        StreamIdentity identity,
        IReadOnlyList<RemuxDbVersion> versions,
        Video primary,
        LibraryOptions libraryOptions
    )
    {
        row.SetGelatoData("infoHash", identity.InfoHash);
        row.SetGelatoData("fileIdx", identity.FileIdx);
        row.SetGelatoData("size", identity.Size);

        var source = row.GelatoData<string>("mediaInfo");
        if (source == SourceProbe)
            return null;

        // Rows probed before sources were recorded, looked for once per row.
        if (source is null && !row.GelatoData<bool>("probeChecked"))
        {
            row.SetGelatoData("probeChecked", true);
            if (!isNewRow && HasProbedStreams(row.Id))
            {
                row.SetGelatoData("mediaInfo", SourceProbe);
                return null;
            }
        }

        var version = RemuxDbMapper.Match(versions, identity);
        if (version is null || !RemuxDbMapper.IsUsable(version))
            return null;

        // A sample, trailer or another cut of a pack filed under the title.
        var runtime = RemuxDbMapper.ToTicks(version.Duration!.Value);
        if (primary.RunTimeTicks is > 0 && !RemuxDbMapper.RuntimeFits(runtime, primary.RunTimeTicks))
        {
            log.LogDebug(
                "RemuxDB match for {Row} runs {Runtime}, {Expected} expected; ignoring it",
                row.Id,
                TimeSpan.FromTicks(runtime),
                TimeSpan.FromTicks(primary.RunTimeTicks.Value)
            );
            return null;
        }

        if (
            source == SourceRemuxDb
            && version.ContentHash is not null
            && row.GelatoData<string>("remuxDbHash") == version.ContentHash
            && row.RunTimeTicks == runtime
        )
        {
            return null;
        }

        var streams = RemuxDbMapper.ToMediaStreams(version, libraryOptions.AllowEmbeddedSubtitles);
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (video is null)
            return null;

        row.RunTimeTicks = runtime;
        row.Container = RemuxDbMapper.NormalizeContainer(version.Container, streams);
        row.Size = version.Size is > 0 ? version.Size : identity.Size;
        row.TotalBitrate = version.Bitrate is > 0 and <= int.MaxValue ? (int)version.Bitrate : null;
        row.Width = video.Width ?? 0;
        row.Height = video.Height ?? 0;
        row.DefaultVideoStreamIndex = video.Index;
        row.HasSubtitles = streams.Any(s => s.Type == MediaStreamType.Subtitle);
        row.SetGelatoData("mediaInfo", SourceRemuxDb);
        row.SetGelatoData("remuxDbHash", version.ContentHash);

        return new PendingMediaInfo(row.Id, streams, ToChapters(version));
    }

    /// <summary>Saves tracks and chapters <see cref="Apply"/> prepared.</summary>
    public void Save(IEnumerable<PendingMediaInfo> pending, CancellationToken ct)
    {
        foreach (var info in pending)
        {
            mediaStreams.SaveMediaStreams(info.ItemId, info.Streams, ct);
            if (info.Chapters.Count > 0)
                chapters.SaveChapters(info.ItemId, info.Chapters);
        }
    }

    /// <summary>
    /// Called after a row's stream was probed: marks its media info as probed and queues it for
    /// RemuxDB when the file's torrent is known. False when the probe found no video.
    /// </summary>
    public bool OnProbed(Video row, LibraryOptions libraryOptions)
    {
        var streams = mediaStreams.GetMediaStreams(new MediaStreamQuery { ItemId = row.Id });
        if (row.RunTimeTicks is not > 0 || streams.All(s => s.Type != MediaStreamType.Video))
            return false;

        var wasRemuxDb = row.GelatoData<string>("mediaInfo") == SourceRemuxDb;
        row.SetGelatoData("mediaInfo", SourceProbe);

        // A file RemuxDB knew: nothing to send.
        if (!Contribute || wasRemuxDb)
            return true;

        // The probe leaves out subtitles the library does not allow: the list is not the file's.
        if (libraryOptions.AllowEmbeddedSubtitles != EmbeddedSubtitleOptions.AllowAll)
            return true;

        // Addons list trailers and samples under a title too (a teaser as its own release). Only
        // a file as long as the title is the title: one without a known runtime is not sent.
        var expected = row.PrimaryVersionId is { } owner
            ? libraryManager.GetItemById(owner)?.RunTimeTicks
            : null;
        if (!RemuxDbMapper.RuntimeFits(row.RunTimeTicks.Value, expected))
        {
            log.LogDebug(
                "Not submitting {Row} to RemuxDB: it runs {Runtime}, the title {Expected}",
                row.Id,
                TimeSpan.FromTicks(row.RunTimeTicks.Value),
                expected is > 0 ? TimeSpan.FromTicks(expected.Value) : "unknown"
            );
            return true;
        }

        var submission = BuildSubmission(row, streams);
        var title = RemuxDbTitle.FromStremioId(row.GetProviderId("Stremio"));
        if (submission is null || title is null)
            return true;

        // The ids link the file to RemuxDB's media table, which holds movies and shows but no
        // episodes: an episode goes by its show's ids with season and episode. With the episode's
        // own ids RemuxDB took the submission but never listed it.
        BaseItem? media = row is Episode { SeriesId: var seriesId } && seriesId != Guid.Empty
            ? libraryManager.GetItemById(seriesId)
            : row;
        var imdb = media?.GetProviderId(MetadataProvider.Imdb);
        submission.ExternalIds.ImdbId = string.IsNullOrEmpty(imdb) ? title.ImdbId : imdb;
        submission.ExternalIds.TmdbId = media is null ? null : IntId(media, MetadataProvider.Tmdb);
        submission.ExternalIds.TvdbId = media is null ? null : IntId(media, MetadataProvider.Tvdb);
        submission.Season = title.Season;
        submission.Episode = title.Episode;
        contributor.Enqueue(title, submission);
        return true;
    }

    /// <summary>The row's stored streams.</summary>
    public IReadOnlyList<MediaStream> GetStreams(Guid itemId) =>
        mediaStreams.GetMediaStreams(new MediaStreamQuery { ItemId = itemId });

    /// <summary>Puts back streams a failed probe replaced.</summary>
    public void RestoreStreams(Guid itemId, IReadOnlyList<MediaStream> streams) =>
        mediaStreams.SaveMediaStreams(itemId, streams, CancellationToken.None);

    /// <summary>
    /// Logs where a probe of a file RemuxDB described disagrees with it: the match picked another
    /// file, or RemuxDB's entry is wrong.
    /// </summary>
    public void CompareWithProbe(
        Guid itemId,
        IReadOnlyList<MediaStream> remuxDb,
        IReadOnlyList<MediaStream> probed
    )
    {
        var expected = Shape(remuxDb);
        var actual = Shape(probed);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation(
                "RemuxDB media info of {Row} differs from its probe: {RemuxDb} | probed {Probe}",
                itemId,
                expected,
                actual
            );
        }

        static string Shape(IReadOnlyList<MediaStream> streams) =>
            string.Join(
                " ",
                streams
                    .Where(s =>
                        !s.IsExternal
                        && s.Type
                            is MediaStreamType.Video
                                or MediaStreamType.Audio
                                or MediaStreamType.Subtitle
                    )
                    .Select(s =>
                        s.Type == MediaStreamType.Video
                            ? $"{s.Codec}/{s.Height}"
                            : $"{s.Type}:{s.Codec}"
                    )
            );
    }

    private static RemuxDbSubmission? BuildSubmission(Video row, IReadOnlyList<MediaStream> streams)
    {
        var infoHash = row.GelatoData<string>("infoHash");
        var size = row.GelatoData<long?>("size");
        var filename = row.GelatoData<string>("filename");
        if (
            string.IsNullOrEmpty(infoHash)
            || size is not > 0
            || string.IsNullOrWhiteSpace(filename)
            || string.IsNullOrEmpty(row.Container)
        )
        {
            return null;
        }

        var tracks = RemuxDbMapper.ToTracks(streams);
        if (tracks is null)
            return null;

        var submission = new RemuxDbSubmission
        {
            Kind = row is Episode ? "episode" : "movie",
            Filename = filename,
            TorrentInfoHash = infoHash,
            TorrentFileIdx = row.GelatoData<int?>("fileIdx"),
            Container = row.Container,
            Size = size.Value,
            Duration = (double)row.RunTimeTicks!.Value / TimeSpan.TicksPerSecond,
            Bitrate = row.TotalBitrate is > 0 ? row.TotalBitrate : null,
            Tracks = tracks,
        };

        return submission;
    }

    private static int? IntId(BaseItem item, MetadataProvider provider) =>
        int.TryParse(
            item.GetProviderId(provider),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var id
        )
            ? id
            : null;

    private bool HasProbedStreams(Guid itemId) =>
        mediaStreams
            .GetMediaStreams(
                new MediaStreamQuery { ItemId = itemId, Type = MediaStreamType.Video }
            )
            .Count > 0;

    private static List<ChapterInfo> ToChapters(RemuxDbVersion version)
    {
        if (version.VirtualChapters || version.Chapters is not { Count: > 1 })
            return [];

        return version
            .Chapters.OrderBy(c => c.StartTime)
            .Select(
                (c, i) =>
                    new ChapterInfo
                    {
                        StartPositionTicks = RemuxDbMapper.ToTicks(Math.Max(0, c.StartTime)),
                        Name =
                            string.IsNullOrWhiteSpace(c.Title) || TimeSpan.TryParse(c.Title, out _)
                                ? string.Create(CultureInfo.InvariantCulture, $"Chapter {i + 1}")
                                : c.Title,
                    }
            )
            .ToList();
    }
}

/// <summary>Tracks and chapters for a row, saved once the row is in the database.</summary>
public sealed record PendingMediaInfo(
    Guid ItemId,
    IReadOnlyList<MediaStream> Streams,
    IReadOnlyList<ChapterInfo> Chapters
);
