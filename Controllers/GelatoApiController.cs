#pragma warning disable SA1611, SA1591, SA1615, CS0165

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;

namespace Gelato.Controllers;

[ApiController]
[Route("gelato")]
public sealed class GelatoApiController : ControllerBase
{
    private readonly ILogger<GelatoApiController> _log;
    private readonly GelatoManager _gelatoManager;
    private readonly string _downloadPath;

    public GelatoApiController(
        ILogger<GelatoApiController> log,
        IApplicationPaths appPaths,
        GelatoManager gelatoManager
    )
    {
        _log = log;
        _gelatoManager = gelatoManager;
        _downloadPath = Path.Combine(appPaths.CachePath, "gelato-torrents");
        Directory.CreateDirectory(_downloadPath);
    }

    [HttpGet("meta/{stremioMetaType}/{Id}")]
    [Authorize]
    public async Task<ActionResult<StremioMeta>> GelatoMeta(
        [FromRoute, Required] StremioMediaType stremioMetaType,
        [FromRoute, Required] string id
    )
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
        var meta = await cfg.Stremio.GetMetaAsync(id, stremioMetaType);
        if (meta is null)
        {
            return NotFound();
        }
        return meta;
    }

    // [HttpGet("catalogs")]
    // Moved to CatalogController

    [HttpGet("subtitles/{itemId:guid}")]
    public ActionResult<IEnumerable<StremioSubtitle>> GetSubtitles(
        [FromRoute, Required] Guid itemId
    )
    {
        var subs = _gelatoManager.GetStremioSubtitlesCache(itemId);
        return Ok(subs ?? new List<StremioSubtitle>());
    }

    [HttpGet("stream")]
    public async Task<IActionResult> TorrentStream(
        [FromQuery] string ih,
        [FromQuery] int? idx,
        [FromQuery] string? filename,
        [FromQuery] string? trackers
    )
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (
            remoteIp == null
            || !(
                IPAddress.IsLoopback(remoteIp)
                || remoteIp.Equals(HttpContext.Connection.LocalIpAddress)
            )
        )
            return Forbid();

        if (string.IsNullOrWhiteSpace(ih))
            return BadRequest("Missing ?ih=<infohash or magnet>");

        var ct = HttpContext.RequestAborted;

        var settings = new EngineSettingsBuilder
        {
            MaximumConnections = 40,
            MaximumDownloadRate = GelatoPlugin.Instance!.Configuration.P2PDLSpeed,
            MaximumUploadRate = GelatoPlugin.Instance.Configuration.P2PULSpeed,
        }.ToSettings();

        var engine = new ClientEngine(settings);

        var infoHashes =
            TryParseInfoHashes(ih)
            ?? throw new ArgumentException("Invalid infohash or magnet.", nameof(ih));
        var announce = ParseTrackers(trackers) ?? DefaultTrackers();
        var magnet = new MagnetLink(infoHashes, name: null, announceUrls: announce);

        var manager = await engine.AddStreamingAsync(magnet, _downloadPath);
        await manager.StartAsync();

        if (!manager.HasMetadata)
        {
            while (!manager.HasMetadata && !ct.IsCancellationRequested)
                await Task.Delay(100, ct);

            if (!manager.HasMetadata)
                return StatusCode(503, "Metadata not yet available.");
        }

        var selected =
            idx is { } i and >= 0 && i < manager.Files.Count
                ? manager.Files[i]
                : (
                    !string.IsNullOrWhiteSpace(filename)
                        ? manager.Files.FirstOrDefault(x =>
                            x.Path.EndsWith(filename, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(
                                Path.GetFileName(x.Path),
                                filename,
                                StringComparison.OrdinalIgnoreCase
                            )
                        ) ?? PickHeuristic(manager)
                        : PickHeuristic(manager)
                );

        var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = new Timer(
            _ =>
            {
                _log.LogDebug(
                    "file: {File}, progress: {Progress:0.00}%, dl: {DL}/s, ul: {UL}/s, peers: {Peers}, seeds: {Seeds}, leechers: {Leechs}, bytes: {Bytes}",
                    selected.Path,
                    manager.Progress,
                    manager.Monitor.DownloadRate,
                    manager.Monitor.UploadRate,
                    manager.Peers.Available,
                    manager.Peers.Seeds,
                    manager.Peers.Leechs,
                    manager.Monitor.DataBytesReceived
                );
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10)
        );

        _log.LogInformation($"starting torrent stream for {selected.Path}");
        var stream = await manager.StreamProvider.CreateStreamAsync(selected, ct);

        // Register cleanup for both normal completion and cancellation
        ct.Register(() =>
        {
            _log.LogInformation("Client disconnected. Cleaning up resources...");
            try
            {
                timerCts.Cancel();
            }
            catch
            {
                // ignored
            }

            try
            {
                timer.Dispose();
            }
            catch
            {
                // ignored
            }

            try
            {
                manager.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // ignored
            }

            try
            {
                engine.Dispose();
            }
            catch
            {
                // ignored
            }
        });

        Response.Headers.AcceptRanges = "bytes";
        return File(stream, GuessContentType(selected.Path), enableRangeProcessing: true);
    }

    private static ITorrentManagerFile PickHeuristic(TorrentManager manager)
    {
        return manager.Files.OrderByDescending(LikelyVideo).ThenByDescending(f => f.Length).First();

        static bool LikelyVideo(ITorrentManagerFile f)
        {
            var name = Path.GetFileName(f.Path);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (name.Contains("sample", StringComparison.OrdinalIgnoreCase))
                return false;
            if (
                ext
                is ".srt"
                    or ".ass"
                    or ".ssa"
                    or ".sub"
                    or ".idx"
                    or ".nfo"
                    or ".txt"
                    or ".jpg"
                    or ".jpeg"
                    or ".png"
                    or ".gif"
            )
                return false;
            return ext
                is ".mkv"
                    or ".mp4"
                    or ".m4v"
                    or ".avi"
                    or ".mov"
                    or ".wmv"
                    or ".ts"
                    or ".m2ts";
        }
    }

    private static InfoHashes? TryParseInfoHashes(string s)
    {
        s = s.Trim();

        if (Regex.IsMatch(s, "^[A-Fa-f0-9]{40}$"))
            return InfoHashes.FromInfoHash(InfoHash.FromHex(s));

        if (Regex.IsMatch(s, "^[A-Z2-7=]+$", RegexOptions.IgnoreCase))
            return InfoHashes.FromInfoHash(InfoHash.FromBase32(s));

        if (Regex.IsMatch(s, "^[A-Fa-f0-9]{64}$"))
            return InfoHashes.FromInfoHash(InfoHash.FromHex(s));

        if (MagnetLink.TryParse(s, out var m))
            return m.InfoHashes;

        return null;
    }

    private static string[]? ParseTrackers(string? trackers) =>
        string.IsNullOrWhiteSpace(trackers)
            ? null
            : Uri.UnescapeDataString(trackers)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] DefaultTrackers() =>
        [
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://explodie.org:6969/announce",
            "udp://tracker.openbittorrent.com:6969/announce",
        ];

    private static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".ts" or ".m2ts" => "video/mp2t",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            _ => "application/octet-stream",
        };
    }
}
