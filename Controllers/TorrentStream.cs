#pragma warning disable SA1611, SA1591, SA1615, CS0165

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using MediaBrowser.Common.Configuration;

using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Streaming;

namespace Gelato;

[ApiController]
//[Authorize]
[Route("gelato")]
public sealed class GelatoApiController : ControllerBase
{
    private readonly ILogger<GelatoApiController> _log;
    private readonly IApplicationPaths _appPaths;
    private readonly string _downloadPath;

    public GelatoApiController(ILogger<GelatoApiController> log, IApplicationPaths appPaths)
    {
        _log = log;
        _appPaths = appPaths;
        _downloadPath = Path.Combine(_appPaths.CachePath, "gelato-torrents");
        Directory.CreateDirectory(_downloadPath);
    }

    [HttpGet("stream")]
    public async Task<IActionResult> Stream(
        [FromQuery] string ih,
        [FromQuery] int? idx,
        [FromQuery] string? filename,
        [FromQuery] string? trackers)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
    // poor man security
    if (remoteIp == null ||
        !(IPAddress.IsLoopback(remoteIp) || remoteIp.Equals(HttpContext.Connection.LocalIpAddress)))
    {
        return Forbid();
    }

        if (string.IsNullOrWhiteSpace(ih))
            return BadRequest("Missing ?ih=<infohash or magnet>");

        var ct = HttpContext.RequestAborted;

        var settings = new EngineSettingsBuilder
        {
            MaximumConnections = 40,
            MaximumDownloadRate = GelatoPlugin.Instance!.Configuration.P2PDLSpeed,
            MaximumUploadRate = GelatoPlugin.Instance!.Configuration.P2PULSpeed,
        }.ToSettings();

        var engine = new ClientEngine(settings);

        var infoHashes = TryParseInfoHashes(ih)
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

        ITorrentManagerFile selected =
            (idx is int i && i >= 0 && i < manager.Files.Count) ? manager.Files[i]
          : (!string.IsNullOrWhiteSpace(filename)
                ? manager.Files.FirstOrDefault(x =>
                      x.Path.EndsWith(filename, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(Path.GetFileName(x.Path), filename, StringComparison.OrdinalIgnoreCase))
                  ?? PickHeuristic(manager)
                : PickHeuristic(manager));

        System.Threading.Timer timer = new((e) =>
        {
            _log.LogInformation("file: " + selected.Path +
                ", progress: " + manager.Progress +
                ", download speed: " + manager.Monitor.DownloadRate +
                ", upload speed: " + manager.Monitor.UploadRate +
                ", peers: " + manager.Peers.Available +
                ", seeds: " + manager.Peers.Seeds +
                ", leechers: " + manager.Peers.Leechs +
                ", downloaded: " + manager.Monitor.DataBytesReceived);
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));

        var stream = await manager.StreamProvider.CreateStreamAsync(selected, ct);

        HttpContext.Response.OnCompleted(async () =>
        {
            try { await stream.DisposeAsync(); } catch { }
            try { await manager.StopAsync(); } catch { }
            try { engine.Dispose(); } catch { }
            try { timer.Dispose(); } catch { }
        });

        Response.Headers["Accept-Ranges"] = "bytes";
        return File(stream, GuessContentType(selected.Path), enableRangeProcessing: true);
    }

    private static ITorrentManagerFile PickHeuristic(TorrentManager manager)
    {
        static bool LikelyVideo(ITorrentManagerFile f)
        {
            var name = Path.GetFileName(f.Path);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (name.Contains("sample", StringComparison.OrdinalIgnoreCase)) return false;
            if (ext is ".srt" or ".ass" or ".ssa" or ".sub" or ".idx" or ".nfo" or ".txt" or ".jpg" or ".jpeg" or ".png" or ".gif") return false;
            return ext is ".mkv" or ".mp4" or ".m4v" or ".avi" or ".mov" or ".wmv" or ".ts" or ".m2ts";
        }

        return manager.Files
            .OrderByDescending(LikelyVideo)
            .ThenByDescending(f => f.Length)
            .First();
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

    private static string[]? ParseTrackers(string? trackers)
    => string.IsNullOrWhiteSpace(trackers)
        ? null
        : Uri
            .UnescapeDataString(trackers)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
  
    private static string[] DefaultTrackers() => new[]
    {
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://explodie.org:6969/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
    };

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