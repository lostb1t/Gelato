#pragma warning disable
using System;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public enum ImdbTitleType
{
    Unknown,
    Movie,
    Short,
    TvEpisode,
    TvMiniSeries,
    TvMovie,
    TvPilot,
    TvSeries,
    TvShort,
    TvSpecial,
    Video,
    VideoGame
}

public class ImdbBasicTitle
{
    // title.basics.tsv
    public string Tconst { get; set; } = default!;            // e.g. tt0133093
    public ImdbTitleType TitleType { get; set; } = ImdbTitleType.Unknown;
    public string PrimaryTitle { get; set; } = default!;
    public string OriginalTitle { get; set; } = default!;
    public bool IsAdult { get; set; }
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
    public int? RuntimeMinutes { get; set; }
    public string[] Genres { get; set; } = Array.Empty<string>();

    // Optional episode linkage (from title.episode.tsv)
    public string? ParentTconst { get; set; }                 // series id
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    public override string ToString() => $"{Tconst} [{TitleType}] {PrimaryTitle} ({StartYear})";
}

public static class Imdb
{
    private const string BasicsUrl = "https://datasets.imdbws.com/title.basics.tsv.gz";
    private const string EpisodeUrl = "https://datasets.imdbws.com/title.episode.tsv.gz";

    /// Download the latest title.basics.tsv.gz to destDir and return its full path.
    public static async Task<string> DownloadBasicsAsync(string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, "title.basics.tsv.gz");
        await DownloadAsync(BasicsUrl, destPath, ct);
        return destPath;
    }

    /// Stream-read a gzipped TSV file and yield lines lazily.
    public static async IAsyncEnumerable<string> ReadLinesAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line != null) yield return line;
        }
    }

    public static async IAsyncEnumerable<ImdbBasicTitle> StreamImdbAsync(
        string cacheDir,
        bool includeEpisodeLinkage = true,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);

        // Ensure basics file exists
        var basicsGzPath = Path.Combine(cacheDir, "title.basics.tsv.gz");
        if (!File.Exists(basicsGzPath))
            await DownloadAsync(BasicsUrl, basicsGzPath, ct);

        // Optional: build episodes linkage (this is the only mem-heavy part)
        Dictionary<string, (string parent, int? season, int? ep)>? episodeMap = null;
        if (includeEpisodeLinkage)
        {
            var episodeGzPath = Path.Combine(cacheDir, "title.episode.tsv.gz");
            if (!File.Exists(episodeGzPath))
                await DownloadAsync(EpisodeUrl, episodeGzPath, ct);

            episodeMap = new(StringComparer.Ordinal);
            await foreach (var line in ReadLinesAsync(episodeGzPath, ct))
            {
                if (line.StartsWith("tconst\t", StringComparison.Ordinal)) continue;
                var cols = line.Split('\t');
                if (cols.Length < 4) continue;

                var tconst = cols[0];
                var parent = cols[1];
                var season = ParseNullableInt(cols[2]);
                var ep = ParseNullableInt(cols[3]);
                if (parent != @"\N")
                    episodeMap[tconst] = (parent, season, ep);
            }
        }

        await foreach (var line in ReadLinesAsync(basicsGzPath, ct))
        {
            if (line.StartsWith("tconst\t", StringComparison.Ordinal)) continue;
            var c = line.Split('\t');
            if (c.Length < 9) continue;

            var rec = new ImdbBasicTitle
            {
                Tconst = c[0],
                TitleType = ParseTitleType(c[1]),
                PrimaryTitle = NullToEmpty(c[2]),
                OriginalTitle = NullToEmpty(c[3]),
                IsAdult = c[4] == "1",
                StartYear = ParseNullableInt(c[5]),
                EndYear = ParseNullableInt(c[6]),
                RuntimeMinutes = ParseNullableInt(c[7]),
                Genres = ParseGenres(c[8])
            };

            if (includeEpisodeLinkage
                && rec.TitleType == ImdbTitleType.TvEpisode
                && episodeMap != null
                && episodeMap.TryGetValue(rec.Tconst, out var info))
            {
                rec.ParentTconst = info.parent;
                rec.SeasonNumber = info.season;
                rec.EpisodeNumber = info.ep;
            }

            yield return rec;
        }
    }

    private static string NullToEmpty(string s) => s == @"\N" ? string.Empty : s;

    private static int? ParseNullableInt(string s)
        => (string.IsNullOrEmpty(s) || s == @"\N")
            ? null
            : (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null);

    private static string[] ParseGenres(string s)
        => (string.IsNullOrEmpty(s) || s == @"\N")
            ? Array.Empty<string>()
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ImdbTitleType ParseTitleType(string s) => s?.ToLowerInvariant() switch
    {
        "movie" => ImdbTitleType.Movie,
        "short" => ImdbTitleType.Short,
        "tvepisode" => ImdbTitleType.TvEpisode,
        "tvminiseries" => ImdbTitleType.TvMiniSeries,
        "tvmovie" => ImdbTitleType.TvMovie,
        "tvpilot" => ImdbTitleType.TvPilot,
        "tvseries" => ImdbTitleType.TvSeries,
        "tvshort" => ImdbTitleType.TvShort,
        "tvspecial" => ImdbTitleType.TvSpecial,
        "video" => ImdbTitleType.Video,
        "videogame" => ImdbTitleType.VideoGame,
        _ => ImdbTitleType.Unknown
    };

    private static async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var client = new HttpClient();
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await resp.Content.CopyToAsync(fs, ct);
    }
}
