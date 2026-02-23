using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato;

public sealed class StremioUri {
    public StremioMediaType MediaType { get; }
    public string ExternalId { get; }
    private readonly string? _streamId;

    public StremioUri(StremioMediaType mediaType, string? externalId, string? streamId = null) {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("externalId cannot be null or empty.", nameof(externalId));

        MediaType = mediaType;
        ExternalId = externalId;
        _streamId = string.IsNullOrWhiteSpace(streamId) ? null : streamId;
    }

    public static StremioUri? FromBaseItem(BaseItem item) {
        ArgumentNullException.ThrowIfNull(item);

        var kind = item.GetBaseItemKind();
        var mediaType = kind switch {
            BaseItemKind.Movie => StremioMediaType.Movie,
            BaseItemKind.Series or BaseItemKind.Episode => StremioMediaType.Series,
            _ => throw new NotSupportedException($"Unsupported BaseItemKind: {kind}"),
        };

        var stremioId = item.GetProviderId("Stremio");
        StremioUri? uri = null;
        if (!string.IsNullOrWhiteSpace(stremioId))
            uri = new StremioUri(mediaType, stremioId);

        switch (kind) {
            case BaseItemKind.Movie: {
                var imdb = item.GetProviderId(MetadataProvider.Imdb);
                return string.IsNullOrWhiteSpace(imdb)
                    ? uri
                    : new StremioUri(StremioMediaType.Movie, imdb);
            }
            case BaseItemKind.Series: {
                var imdb = item.GetProviderId(MetadataProvider.Imdb);
                return string.IsNullOrWhiteSpace(imdb)
                    ? uri
                    : new StremioUri(StremioMediaType.Series, imdb);
            }
            case BaseItemKind.Episode: {
                var ep = (Episode)item;
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
        }

        return null;
    }

    public override string ToString() {
        var type = MediaType == StremioMediaType.Movie ? "movie" : "series";
        return _streamId is null
            ? $"stremio://{type}/{ExternalId}"
            : $"stremio://{type}/{ExternalId}/{_streamId}";
    }

    public Guid ToGuid() {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(ToString()));
        return new Guid(hash);
    }
}

public static class Utils {
    public static long? ParseToTicks(string? input) {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim().ToLowerInvariant();

        // Try built-in parse (hh:mm:ss)
        if (TimeSpan.TryParse(input, out var ts))
            return ts.Ticks;

        // Try XML ISO8601 style (PT2H29M)
        try {
            ts = System.Xml.XmlConvert.ToTimeSpan(input);
            return ts.Ticks;
        }
        catch {
            // ignore
        }
        // Regex fallback for human formats like "2h29min"
        var h = Regex.Match(input, @"(\d+)\s*h");
        var m = Regex.Match(input, @"(\d+)\s*min");
        var s = Regex.Match(input, @"(\d+)\s*s(ec)?");

        var hours = h.Success ? int.Parse(h.Groups[1].Value) : 0;
        var mins = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        var secs = s.Success ? int.Parse(s.Groups[1].Value) : 0;

        // If plain number like "149" → minutes
        if (!h.Success && !m.Success && !s.Success && int.TryParse(input, out var onlyNum))
            mins = onlyNum;

        return new TimeSpan(hours, mins, secs).Ticks;
    }
}

public sealed class KeyLock {
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _queues = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _inflight = new();

    public Task RunSingleFlightAsync(
        Guid key,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default
    ) {
        var lazy = _inflight.GetOrAdd(
            key,
            _ => new Lazy<Task>(
                () => Once(key, action, ct),
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );
        return lazy.Value;
    }

    public async Task RunQueuedAsync(
        Guid key,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default
    ) {
        var sem = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try {
            await action(ct).ConfigureAwait(false);
        }
        finally {
            ReleaseAndMaybeRemove(key, sem);
        }
    }

    private async Task Once(Guid key, Func<CancellationToken, Task> action, CancellationToken ct) {
        try {
            await action(ct).ConfigureAwait(false);
        }
        finally {
            _inflight.TryRemove(key, out _);
        }
    }

    private void ReleaseAndMaybeRemove(Guid key, SemaphoreSlim sem) {
        sem.Release();
        if (sem.CurrentCount == 1 && sem.Wait(0)) {
            sem.Release();
            _queues.TryRemove(key, out _);
        }
    }
}

public static class EnumMappingExtensions {
    public static StremioMediaType ToStremio(this BaseItemKind kind) {
        return kind switch {
            BaseItemKind.Movie => StremioMediaType.Movie,
            BaseItemKind.Series or BaseItemKind.Season or BaseItemKind.Episode => StremioMediaType.Series,
            _ => StremioMediaType.Unknown,
        };
    }

    public static BaseItemKind ToBaseItem(this StremioMediaType type) {
        return type switch {
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

public static class ActionContextExtensions {
    private static readonly string[] RouteGuidKeys =
    [
        "id",
        "Id",
        "ID",
        "itemId",
        "ItemId",
        "ItemID"
    ];

    private static readonly HashSet<string> SearchActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetItems",
        "GetItemsByUserIdLegacy",
    };

    private static readonly HashSet<string> BaseItemListActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetItems",
        "GetItemsByUserIdLegacy",
        "NextUp",
        "GetResumeItems",
        "GetResumeItemsLegacy",
        "GetNextUp",
        "GetEpisodes",
        "GetSimilarItems",
        "GetLatestMedia",
        "GetLatestMediaLegacy",
        "GetUpcomingEpisodes",
        "GetRecommendedItems",
        "GetMovieRecommendations",
        "GetSuggestionsLegacy",
        "GetSuggestions",
        "GetItemCounts"
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
        "GetPostedPlaybackInfo",
        "GetVideoStream",
        "GetDownload",
        "GetSubtitleWithTicks",
    };

    private static readonly HashSet<string> InsertableListActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetItems",
        "GetItemsByUserIdLegacy",
    };

    public static string? GetActionName(this ActionExecutingContext ctx) =>
        (ctx.ActionDescriptor as ControllerActionDescriptor)?.ActionName;

    public static string? GetActionName(this HttpContext ctx) =>
        ctx.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ActionName;

    public static bool IsApiListing(this HttpContext ctx) {
        var actionName = ctx.GetActionName();
        return actionName != null && BaseItemListActionNames.Contains(actionName);
    }

    public static bool IsApiSearchAction(this ActionExecutingContext ctx) =>
        ctx.GetActionName() is { } actionName && SearchActionNames.Contains(actionName);

    public static bool IsInsertableAction(this HttpContext ctx) {
        var actionName = ctx.GetActionName();
        return actionName != null
            && InsertableActionNames.Contains(actionName)
            && (
                !InsertableListActionNames.Contains(actionName)
                || InsertableListActionNames.Contains(actionName) && IsSingleItemList(ctx)
            );
    }

    public static bool IsInsertableAction(this ActionExecutingContext ctx) => ctx.HttpContext.IsInsertableAction();

    private static bool IsSingleItemList(HttpContext ctx) {
        var q = ctx.Request.Query;
        if (!q.TryGetValue("ids", out var idsRaw))
            return false;

        var ids = idsRaw
            .SelectMany(v =>
                v.ToString()
                    .Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
            )
            .ToArray();

        return ids.Length == 1;
    }

    public static bool TryGetRouteGuid(this ActionExecutingContext ctx, out Guid value) {
        value = Guid.Empty;
        return ctx.TryGetRouteGuidString(out var s) && Guid.TryParse(s, out value);
    }

    private static bool TryGetRouteGuidString(this ActionExecutingContext ctx, out string value) {
        value = string.Empty;

        // Check if already resolved
        if (ctx.HttpContext.Items["GuidResolved"] is Guid g) {
            value = g.ToString("N");
            return true;
        }

        var rd = ctx.RouteData.Values;

        // Check route values
        foreach (var key in RouteGuidKeys) {
            if (
                rd.TryGetValue(key, out var raw)
                && raw?.ToString() is { } s
                && !string.IsNullOrWhiteSpace(s)
            ) {
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
        ) {
            value = ids[0]!;
            return true;
        }

        return false;
    }

    public static void ReplaceGuid(this ActionExecutingContext ctx, Guid value) {
        var rd = ctx.RouteData.Values;

        foreach (var key in RouteGuidKeys) {
            if (rd.TryGetValue(key, out var raw) && raw is not null) {
                rd[key] = value.ToString();
                ctx.ActionArguments[key] = value;
            }
        }

        ctx.HttpContext.Items["GuidResolved"] = value;
    }

    public static bool TryGetUserId(this ActionExecutingContext ctx, out Guid userId) {
        return ctx.HttpContext.TryGetUserId(out userId);
    }

    public static bool TryGetUserId(this HttpContext ctx, out Guid userId) {
        userId = Guid.Empty;

        var userIdStr =
            ctx.User.Claims.FirstOrDefault(c => c.Type is "UserId" or "Jellyfin-UserId")?.Value
            ?? ctx.Request.Query["userId"].FirstOrDefault();

        if (!Guid.TryParse(userIdStr, out userId))
            return false;

        return userId != Guid.Empty;
    }

    public static bool TryGetActionArgument<T>(
        this ActionExecutingContext ctx,
        string key,
        out T value,
        T defaultValue = default
    ) {
        if (ctx.ActionArguments.TryGetValue(key, out var objValue) && objValue is T typedValue) {
            value = typedValue;
            return true;
        }

        value = defaultValue;
        return false;
    }
}

public static class BaseItemExtensions {
    public static bool HasStreamTag(this BaseItem item) {
        return item.Tags is not null && item.Tags.Contains(GelatoManager.StreamTag, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsPrimaryVersion(this BaseItem item) {
        return !item.HasStreamTag() && string.IsNullOrWhiteSpace((item as Video)?.PrimaryVersionId) && !item.IsVirtualItem;
    }

    public static bool IsStream(this BaseItem item) {
        return !string.IsNullOrWhiteSpace(item.GetProviderId("Stremio")) && !item.IsPrimaryVersion();
    }

    public static T? GelatoData<T>(this BaseItem item, string key) {
        if (string.IsNullOrEmpty(item.ExternalId))
            return default;

        try {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.ExternalId);
            return dict != null && dict.TryGetValue(key, out var el)
                ? el.Deserialize<T>()
                : default;
        }
        catch {
            return default;
        }
    }

    public static void SetGelatoData<T>(this BaseItem item, string key, T value) {
        Dictionary<string, JsonElement> data;

        try {
            data = string.IsNullOrEmpty(item.ExternalId)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.ExternalId)
                  ?? new Dictionary<string, JsonElement>();
        }
        catch {
            data = new Dictionary<string, JsonElement>();
        }

        data[key] = JsonSerializer.SerializeToElement(value);
        item.ExternalId = JsonSerializer.Serialize(data);
    }
}
