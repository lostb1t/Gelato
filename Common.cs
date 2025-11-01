using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Filters;

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

    private static readonly Regex Rx = new(
        @"^stremio://(?<type>movie|series)/(?<ext>[^/\s]+)(?:/(?<stream>[^/\s]+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    public static StremioUri FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null or empty.", nameof(value));

        if (TryParse(value.ToLowerInvariant(), out var sid) && sid is not null)
            return sid;

        throw new FormatException($"Invalid StremioId string: {value}");
    }

    public static StremioUri? FromMeta(StremioMeta meta)
    {
        if (TryParse(meta.Id, out var sid) && sid is not null)
            return sid;

        return null;
    }

    public static StremioUri? FromBaseItem(BaseItem item)
    {
        if (item is null)
            throw new ArgumentNullException(nameof(item));

        var kind = item.GetBaseItemKind();
        var mediaType = kind switch
        {
            BaseItemKind.Movie => StremioMediaType.Movie,
            BaseItemKind.Series or BaseItemKind.Episode => StremioMediaType.Series,
            _ => throw new NotSupportedException($"Unsupported BaseItemKind: {kind}"),
        };

        var stremioId = item.GetProviderId("Stremio");
        StremioUri? uri = null;
        if (!string.IsNullOrWhiteSpace(stremioId))
            uri = new StremioUri(mediaType, stremioId);

        if (kind == BaseItemKind.Movie)
        {
            var imdb = item.GetProviderId(MetadataProvider.Imdb);
            return string.IsNullOrWhiteSpace(imdb)
                ? uri
                : new StremioUri(StremioMediaType.Movie, imdb);
        }

        if (kind == BaseItemKind.Series)
        {
            var imdb = item.GetProviderId(MetadataProvider.Imdb);
            return string.IsNullOrWhiteSpace(imdb)
                ? uri
                : new StremioUri(StremioMediaType.Series, imdb);
        }

        if (kind == BaseItemKind.Episode)
        {
            var ep = (MediaBrowser.Controller.Entities.TV.Episode)item;
            var seriesImdb = ep.Series?.GetProviderId(MetadataProvider.Imdb);
            if (
                string.IsNullOrWhiteSpace(seriesImdb)
                || ep.ParentIndexNumber is null
                || ep.IndexNumber is null
            )
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
            _ => throw new FormatException($"Unknown media type in StremioId: {typeStr}"),
        };

        return new StremioUri(mediaType, ext, stream);
    }

    public static bool TryParse(string id, out StremioUri? value)
    {
        try
        {
            value = Parse(id);
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    public override string ToString()
    {
        var type = MediaType == StremioMediaType.Movie ? "movie" : "series";
        return StreamId is null
            ? $"stremio://{type}/{ExternalId}"
            : $"stremio://{type}/{ExternalId}/{StreamId}";
    }

    // without stream id
    public string ToBaseString()
    {
        var type = MediaType == StremioMediaType.Movie ? "movie" : "series";
        return $"stremio://{type}/{ExternalId}";
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

    public static StremioUri Movie(string externalId, string? streamId = null) =>
        new(StremioMediaType.Movie, externalId, streamId);

    public static StremioUri Series(string externalId, string? streamId = null) =>
        new(StremioMediaType.Series, externalId, streamId);
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

public sealed class TimedBlock : IDisposable
{
    private readonly Stopwatch _sw;
    private readonly string _label;

    public TimedBlock(string label)
    {
        _label = label;
        _sw = Stopwatch.StartNew();
    }

    public void Dispose()
    {
        _sw.Stop();
        Console.WriteLine($"{_label} took {_sw.ElapsedMilliseconds} ms");
    }
}

public sealed class KeyLock
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _queues = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _inflight = new();

    public Task RunSingleFlightAsync(
        Guid key,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default
    )
    {
        var lazy = _inflight.GetOrAdd(
            key,
            _ => new Lazy<Task>(
                () => Once(key, action, ct),
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );
        return lazy.Value;
    }

    public Task<T> RunSingleFlightAsync<T>(
        Guid key,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default
    )
    {
        var lazy = _inflight.GetOrAdd(
            key,
            _ => new Lazy<Task>(
                () => Once(key, action, ct),
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );
        return (Task<T>)lazy.Value;
    }

    public async Task RunQueuedAsync(
        Guid key,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default
    )
    {
        var sem = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseAndMaybeRemove(key, sem);
        }
    }

    public async Task<T> RunQueuedAsync<T>(
        Guid key,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default
    )
    {
        var sem = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseAndMaybeRemove(key, sem);
        }
    }

    public async Task<bool> TryRunQueuedAsync(
        Guid key,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default
    )
    {
        var sem = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct).ConfigureAwait(false))
            return false;
        try
        {
            await action(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            ReleaseAndMaybeRemove(key, sem);
        }
    }

    public async Task<(bool ran, T result)> TryRunQueuedAsync<T>(
        Guid key,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default
    )
    {
        var sem = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct).ConfigureAwait(false))
            return (false, default!);
        try
        {
            return (true, await action(ct).ConfigureAwait(false));
        }
        finally
        {
            ReleaseAndMaybeRemove(key, sem);
        }
    }

    private async Task Once(Guid key, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try
        {
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private async Task<T> Once<T>(
        Guid key,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct
    )
    {
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
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

public static class EnumMappingExtensions
{
    public static StremioMediaType ToStremio(this BaseItemKind kind)
    {
        return kind switch
        {
            BaseItemKind.Movie => StremioMediaType.Movie,
            BaseItemKind.Series => StremioMediaType.Series,
            BaseItemKind.Season => StremioMediaType.Series,
            BaseItemKind.Episode => StremioMediaType.Series,
            _ => StremioMediaType.Unknown,
        };
    }

    public static BaseItemKind ToBaseItem(this StremioMediaType type)
    {
        return type switch
        {
            StremioMediaType.Movie => BaseItemKind.Movie,
            StremioMediaType.Series => BaseItemKind.Series,
            _ => throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Unknown StremioMediaType"
            ),
        };
    }
}

public static class ActionContextExtensions
{
    private static readonly string[] RouteGuidKeys =
    {
        "id",
        "Id",
        "ID",
        "itemId",
        "ItemId",
        "ItemID",
    };

    private static readonly HashSet<string> SearchActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetItems",
        "GetItemsByUserIdLegacy",
    };

    private static readonly HashSet<string> InsertableActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetItems",
        "GetItem",
        "GetItemLegacy",
        "GetItemsByUserIdLegacy",
        "GetPlaybackInfo",
    };

    public static string? GetActionName(this ActionExecutingContext ctx) =>
        (ctx.ActionDescriptor as ControllerActionDescriptor)?.ActionName;

    public static string? GetActionName(this HttpContext ctx) =>
        ctx.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ActionName;

    public static bool IsApiRequest(this ActionExecutingContext ctx) =>
        ctx.GetActionName() is not null;

    public static bool IsApiRequest(this HttpContext ctx) => ctx.GetActionName() is not null;

    public static bool IsApiListing(this ActionExecutingContext ctx) =>
        ctx.GetActionName() is string actionName && SearchActionNames.Contains(actionName);

    public static bool IsSearchAction(this ActionExecutingContext ctx) =>
        ctx.GetActionName() is string actionName && SearchActionNames.Contains(actionName);

    public static bool IsInsertableAction(this ActionExecutingContext ctx) =>
        ctx.GetActionName() is string actionName && InsertableActionNames.Contains(actionName);

    public static bool TryGetRouteGuid(this ActionExecutingContext ctx, out Guid value)
    {
        value = Guid.Empty;
        return ctx.TryGetRouteGuidString(out var s) && Guid.TryParse(s, out value);
    }

    public static bool TryGetRouteGuidString(this ActionExecutingContext ctx, out string value)
    {
        value = string.Empty;

        // Check if already resolved
        if (ctx.HttpContext.Items["GuidResolved"] is Guid g)
        {
            value = g.ToString("N");
            return true;
        }

        var rd = ctx.RouteData.Values;

        // Check route values
        foreach (var key in RouteGuidKeys)
        {
            if (
                rd.TryGetValue(key, out var raw)
                && raw?.ToString() is string s
                && !string.IsNullOrWhiteSpace(s)
            )
            {
                value = s;
                return true;
            }
        }

        // Fallback: check query string "ids"
        var query = ctx.HttpContext.Request.Query;
        if (
            query.TryGetValue("ids", out var ids)
            && ids.Count == 1
            && !string.IsNullOrWhiteSpace(ids[0])
        )
        {
            value = ids[0]!;
            return true;
        }

        return false;
    }

    public static void ReplaceGuid(this ActionExecutingContext ctx, Guid value)
    {
        var rd = ctx.RouteData.Values;

        foreach (var key in RouteGuidKeys)
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                rd[key] = value.ToString();
                ctx.ActionArguments[key] = value;
            }
        }

        ctx.HttpContext.Items["GuidResolved"] = value;
    }

    public static bool TryGetUserId(this ActionExecutingContext ctx, out Guid userId)
    {
        userId = Guid.Empty;

        var userIdStr = ctx
            .HttpContext.User.Claims.FirstOrDefault(c => c.Type is "UserId" or "Jellyfin-UserId")
            ?.Value;

        return Guid.TryParse(userIdStr, out userId);
    }

    public static bool TryGetActionArgument<T>(
        this ActionExecutingContext ctx,
        string key,
        out T value,
        T defaultValue = default
    )
    {
        if (ctx.ActionArguments.TryGetValue(key, out var objValue) && objValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = defaultValue;
        return false;
    }
}

public static class StringExtensions
{
    public static HashSet<BaseItemKind> ParseBaseItemKinds(this string value)
    {
        var kinds = new HashSet<BaseItemKind>();

        if (string.IsNullOrWhiteSpace(value))
            return kinds;

        foreach (
            var raw in value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (Enum.TryParse<BaseItemKind>(raw, true, out var kind))
                kinds.Add(kind);
        }

        return kinds;
    }
}
