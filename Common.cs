using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Collections.Concurrent;

namespace Gelato.Common;

public sealed class StremioUri
{
    public StremioMediaType MediaType { get; }
    public string ExternalId { get; }
    public string? StreamId { get; }

    public StremioUri(StremioMediaType mediaType, string externalId, string? streamId = null)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("externalId cannot be null or empty.", nameof(externalId));

        MediaType = mediaType;
        ExternalId = externalId;
        StreamId = string.IsNullOrWhiteSpace(streamId) ? null : streamId;
    }

    private static readonly Regex Rx =
        new(@"^stremio://(?<type>movie|series)/(?<ext>[^/\s]+)(?:/(?<stream>[^/\s]+))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static StremioUri? FromBaseItem(BaseItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));

        var kind = item.GetBaseItemKind();
        var mediaType = kind switch
        {
            BaseItemKind.Movie => StremioMediaType.Movie,
            BaseItemKind.Series or BaseItemKind.Episode => StremioMediaType.Series,
            _ => throw new NotSupportedException($"Unsupported BaseItemKind: {kind}")
        };

       var stremioId = item.GetProviderId("Stremio");
       StremioUri? uri = null;
       if (!string.IsNullOrWhiteSpace(stremioId))
         uri = new StremioUri(mediaType, stremioId);

        if (kind == BaseItemKind.Movie)
        {
            var imdb = item.GetProviderId(MetadataProvider.Imdb);
            return string.IsNullOrWhiteSpace(imdb) ? uri : new StremioUri(StremioMediaType.Movie, imdb);
        }

        if (kind == BaseItemKind.Series)
        {
            var imdb = item.GetProviderId(MetadataProvider.Imdb);
            return string.IsNullOrWhiteSpace(imdb) ? uri : new StremioUri(StremioMediaType.Series, imdb);
        }

        if (kind == BaseItemKind.Episode)
        {
            var ep = (MediaBrowser.Controller.Entities.TV.Episode)item;
            var seriesImdb = ep.Series?.GetProviderId(MetadataProvider.Imdb);
            if (string.IsNullOrWhiteSpace(seriesImdb) || ep.ParentIndexNumber is null || ep.IndexNumber is null)
                return uri;

            var ext = $"{seriesImdb}:{ep.ParentIndexNumber}:{ep.IndexNumber}";
            return new StremioUri(StremioMediaType.Series, ext);
        }

        return null;
    }

    public static StremioUri Parse(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Value cannot be null or empty.", nameof(id));

        var m = Rx.Match(id);
        if (!m.Success)
            throw new FormatException($"Invalid StremioId: {id}");

        var typeStr = m.Groups["type"].Value.ToLowerInvariant();
        var ext = m.Groups["ext"].Value;
        var stream = m.Groups["stream"].Success ? m.Groups["stream"].Value : null;

        var mediaType = typeStr switch
        {
            "movie" => StremioMediaType.Movie,
            "series" => StremioMediaType.Series,
            _ => throw new FormatException($"Unknown media type in StremioId: {typeStr}")
        };

        return new StremioUri(mediaType, ext, stream);
    }

    public static bool TryParse(string id, out StremioUri? value)
    {
        try { value = Parse(id); return true; }
        catch { value = null; return false; }
    }

    public override string ToString()
    {
        var type = MediaType == StremioMediaType.Movie ? "movie" : "series";
        return StreamId is null
            ? $"stremio://{type}/{ExternalId}"
            : $"stremio://{type}/{ExternalId}/{StreamId}";
    }

    public Guid ToGuid()
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(ToString()));
        return new Guid(hash);
    }

    // Convenience builders
    public StremioUri WithStream(string streamId) => new(MediaType, ExternalId, streamId);
    public StremioUri WithoutStream() => new(MediaType, ExternalId, null);

    public static StremioUri Movie(string externalId, string? streamId = null)
        => new(StremioMediaType.Movie, externalId, streamId);

    public static StremioUri Series(string externalId, string? streamId = null)
        => new(StremioMediaType.Series, externalId, streamId);
}

public static class Utils
{
    public static long? ParseToTicks(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim().ToLowerInvariant();

        // Try built-in parse (hh:mm:ss)
        if (TimeSpan.TryParse(input, out var ts))
            return ts.Ticks;

        // Try XML ISO8601 style (PT2H29M)
        try
        {
            ts = System.Xml.XmlConvert.ToTimeSpan(input);
            return ts.Ticks;
        }
        catch
        {
            // ignore
        }
        // Regex fallback for human formats like "2h29min"
        var h = Regex.Match(input, @"(\d+)\s*h");
        var m = Regex.Match(input, @"(\d+)\s*min");
        var s = Regex.Match(input, @"(\d+)\s*s(ec)?");

        int hours = h.Success ? int.Parse(h.Groups[1].Value) : 0;
        int mins = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        int secs = s.Success ? int.Parse(s.Groups[1].Value) : 0;

        // If plain number like "149" → minutes
        if (!h.Success && !m.Success && !s.Success && int.TryParse(input, out var onlyNum))
            mins = onlyNum;

        return new TimeSpan(hours, mins, secs).Ticks;
    }
}

public sealed class KeyLock
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _queues = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _inflight = new();

    public Task RunSingleFlightAsync(Guid key, Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task>(() => Once(key, action, ct), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    public Task<T> RunSingleFlightAsync<T>(Guid key, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task>(() => Once(key, action, ct), LazyThreadSafetyMode.ExecutionAndPublication));
        return (Task<T>)lazy.Value;
    }

    public async Task RunQueuedAsync(Guid key, Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        var sem = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try { await action(ct).ConfigureAwait(false); }
        finally { ReleaseAndMaybeRemove(key, sem); }
    }

    public async Task<T> RunQueuedAsync<T>(Guid key, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        var sem = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try { return await action(ct).ConfigureAwait(false); }
        finally { ReleaseAndMaybeRemove(key, sem); }
    }

    public async Task<bool> TryRunQueuedAsync(Guid key, Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        var sem = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct).ConfigureAwait(false)) return false;
        try { await action(ct).ConfigureAwait(false); return true; }
        finally { ReleaseAndMaybeRemove(key, sem); }
    }

    public async Task<(bool ran, T result)> TryRunQueuedAsync<T>(Guid key, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        var sem = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct).ConfigureAwait(false)) return (false, default!);
        try { return (true, await action(ct).ConfigureAwait(false)); }
        finally { ReleaseAndMaybeRemove(key, sem); }
    }

    private async Task Once(Guid key, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try { await action(ct).ConfigureAwait(false); }
        finally { _inflight.TryRemove(key, out _); }
    }

    private async Task<T> Once<T>(Guid key, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try { return await action(ct).ConfigureAwait(false); }
        finally { _inflight.TryRemove(key, out _); }
    }

    private void ReleaseAndMaybeRemove(Guid key, SemaphoreSlim sem)
    {
        sem.Release();
        if (sem.CurrentCount == 1 && sem.Wait(0))
        {
            sem.Release();
            _queues.TryRemove(key, out _);
        }
    }
}