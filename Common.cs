using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato;

public sealed class StremioUri
{
    public StremioMediaType MediaType { get; }
    public string ExternalId { get; }
    private readonly string? _streamId;

    public StremioUri(StremioMediaType mediaType, string? externalId, string? streamId = null)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("externalId cannot be null or empty.", nameof(externalId));

        MediaType = mediaType;
        ExternalId = externalId;
        _streamId = string.IsNullOrWhiteSpace(streamId) ? null : streamId;
    }

    public static StremioUri? FromBaseItem(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

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

        switch (kind)
        {
            case BaseItemKind.Movie:
                {
                    var imdb = item.GetProviderId(MetadataProvider.Imdb);
                    return string.IsNullOrWhiteSpace(imdb)
                        ? uri
                        : new StremioUri(StremioMediaType.Movie, imdb);
                }
            case BaseItemKind.Series:
                {
                    var imdb = item.GetProviderId(MetadataProvider.Imdb);
                    return string.IsNullOrWhiteSpace(imdb)
                        ? uri
                        : new StremioUri(StremioMediaType.Series, imdb);
                }
            case BaseItemKind.Episode:
                {
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

    public override string ToString()
    {
        var type = MediaType == StremioMediaType.Movie ? "movie" : "series";
        return _streamId is null
            ? $"stremio://{type}/{ExternalId}"
            : $"stremio://{type}/{ExternalId}/{_streamId}";
    }

    public Guid ToGuid()
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(ToString()));
        return new Guid(hash);
    }
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

        var hours = h.Success ? int.Parse(h.Groups[1].Value) : 0;
        var mins = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        var secs = s.Success ? int.Parse(s.Groups[1].Value) : 0;

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

/// <summary>
/// Makes URLs safe to write to Jellyfin's log. Stream URLs, addon URLs and the http paths of
/// stream items carry the user's debrid API key or addon config in their path or query, and
/// users paste their logs into public issues and chats.
/// </summary>
public static class Redact
{
    /// <summary>
    /// Reduces an absolute URL to scheme, host and port, dropping user info, path, query and
    /// fragment. Local file paths are returned as they are. Anything else that looks like a URL
    /// but does not parse is replaced entirely, never partially masked.
    /// </summary>
    public static string Url(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile || uri.IsUnc)
                return value;
            return string.IsNullOrEmpty(uri.Authority)
                ? $"{uri.Scheme}:<redacted>"
                : $"{uri.Scheme}://{uri.Authority}/<redacted>";
        }

        return value.Contains("://", StringComparison.Ordinal) ? "<redacted>" : value;
    }
}

public static class EnumMappingExtensions
{
    public static StremioMediaType ToStremio(this BaseItemKind kind)
    {
        return kind switch
        {
            BaseItemKind.Movie => StremioMediaType.Movie,
            BaseItemKind.Series or BaseItemKind.Season or BaseItemKind.Episode =>
                StremioMediaType.Series,
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
    [
        "id",
        "Id",
        "ID",
        "itemId",
        "ItemId",
        "ItemID",
    ];

    private static readonly string[] IdsGuidKeys = ["ids", "Ids", "IDs"];

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
        "GetSeasons",
        "GetSimilarItems",
        "GetLatestMedia",
        "GetLatestMediaLegacy",
        "GetUpcomingEpisodes",
        "GetRecommendedItems",
        "GetMovieRecommendations",
        "GetSuggestionsLegacy",
        "GetSuggestions",
        "GetItemCounts",
        "GetSectionContent",
        // The search bar's suggestion dropdown. It lists library rows through the item repository
        // like any other listing, so without it the unreleased filter and the stream-row exclusion
        // never run for it.
        "GetSearchHints",
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
        // Same stream, under a container extension: a separate action that delegates to
        // GetVideoStream. Clients that play /Videos/{id}/stream.mkv would otherwise get a 404
        // for a search result that was never opened, because nothing materializes it.
        "GetVideoStreamByContainer",
        "GetDownload",
        "GetSubtitleWithTicks",
        // Clients offer these from a search result's context menu, before the result was opened.
        "MarkPlayedItem",
        "MarkPlayedItemLegacy",
        "MarkFavoriteItem",
        "MarkFavoriteItemLegacy",
        "UpdateUserItemRating",
        "UpdateUserItemRatingLegacy",
        "UpdateItemUserData",
        "UpdateItemUserDataLegacy",
    };

    // Jellyfin answers playback info under two actions: GET /Items/{id}/PlaybackInfo and
    // POST /Items/{id}/PlaybackInfo. The web client posts, several native clients use the GET.
    private static readonly HashSet<string> PlaybackInfoActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetPlaybackInfo",
        "GetPostedPlaybackInfo",
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

    public static bool IsApiListing(this HttpContext ctx)
    {
        var actionName = ctx.GetActionName();
        return actionName != null && BaseItemListActionNames.Contains(actionName);
    }

    public static bool IsHomeScreenSectionListing(this HttpContext ctx)
    {
        var action = ctx.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (
            action is not null
            && string.Equals(
                action.ControllerName,
                "HomeScreen",
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(
                action.ActionName,
                "GetSectionContent",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return true;
        }

        var path = ctx.Request.Path;
        return path.HasValue
            && path.Value?.Contains("/HomeScreen/Section/", StringComparison.OrdinalIgnoreCase)
                == true;
    }

    public static bool IsApiSearchAction(this ActionExecutingContext ctx) =>
        ctx.GetActionName() is { } actionName && SearchActionNames.Contains(actionName);

    public static bool IsPlaybackInfoAction(this HttpContext? ctx) =>
        ctx?.GetActionName() is { } actionName && PlaybackInfoActionNames.Contains(actionName);

    public static bool IsInsertableAction(this HttpContext ctx)
    {
        var actionName = ctx.GetActionName();
        return actionName != null
            && InsertableActionNames.Contains(actionName)
            && (
                !InsertableListActionNames.Contains(actionName)
                || InsertableListActionNames.Contains(actionName) && IsSingleItemList(ctx)
            );
    }

    public static bool IsInsertableAction(this ActionExecutingContext ctx) =>
        ctx.HttpContext.IsInsertableAction();

    public static bool IsSingleItemList(this HttpContext ctx)
    {
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

    public static bool IsSingleItemList(this ActionExecutingContext ctx) =>
        ctx.HttpContext.IsSingleItemList();

    /// <summary>
    /// Whether the caller itself asked for specific item ids.
    /// </summary>
    /// <remarks>
    /// Jellyfin 12 resolves a searchTerm to matching ids before it queries the repository, so
    /// InternalItemsQuery.ItemIds is populated for ordinary searches too. Only the query string
    /// distinguishes a caller-supplied id lookup from one the search manager produced.
    /// </remarks>
    public static bool HasExplicitItemIds(this HttpContext ctx) =>
        ctx.Request.Query.ContainsKey("ids");

    public static bool TryGetRouteGuid(this ActionExecutingContext ctx, out Guid value)
    {
        value = Guid.Empty;
        return ctx.TryGetRouteGuidString(out var s) && Guid.TryParse(s, out value);
    }

    private static bool TryGetRouteGuidString(this ActionExecutingContext ctx, out string value)
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
                && raw?.ToString() is { } s
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

    /// <summary>
    /// Replaces every id in the route, the query-bound arguments and id lists that
    /// <paramref name="map"/> knows a replacement for. Returns whether anything changed.
    /// </summary>
    public static bool RedirectGuids(this ActionExecutingContext ctx, Func<Guid, Guid?> map)
    {
        var changed = false;
        foreach (var (key, raw) in ctx.RouteData.Values.ToList())
        {
            if (raw?.ToString() is { } s && Guid.TryParse(s, out var g) && map(g) is { } to)
            {
                ctx.RouteData.Values[key] = to.ToString("N");
                if (ctx.ActionArguments.ContainsKey(key))
                    ctx.ActionArguments[key] = to;
                ctx.HttpContext.Items["GuidResolved"] = to;
                changed = true;
            }
        }

        foreach (var (key, value) in ctx.ActionArguments.ToList())
        {
            switch (value)
            {
                case Guid g when map(g) is { } to:
                    ctx.ActionArguments[key] = to;
                    changed = true;
                    break;
                case Guid[] ids when ids.Any(g => map(g) is not null):
                    ctx.ActionArguments[key] = ids.Select(g => map(g) ?? g).Distinct().ToArray();
                    changed = true;
                    break;
            }
        }

        return changed;
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

        // Also replace ids query-parameter argument (used by list actions like GetItems)
        foreach (var key in IdsGuidKeys)
        {
            if (ctx.ActionArguments.ContainsKey(key))
            {
                ctx.ActionArguments[key] = new[] { value };
                break;
            }
        }

        ctx.HttpContext.Items["GuidResolved"] = value;
    }

    public static bool TryGetUserId(this ActionExecutingContext ctx, out Guid userId)
    {
        return ctx.HttpContext.TryGetUserId(out userId);
    }

    /// <summary>
    /// The user a request acts for: the one its token authenticated, or, when the token carries
    /// none, the one the caller names in the query or in the route.
    /// </summary>
    /// <remarks>
    /// An API key authenticates without a user, and Jellyfin still puts a UserId claim on the
    /// request: the empty guid. Taking the claim as the answer hid the user the caller had
    /// named, and a search result opened with an API key materialized for nobody: the request
    /// reached Jellyfin with the synthetic id and answered 404.
    /// </remarks>
    public static bool TryGetUserId(this HttpContext ctx, out Guid userId)
    {
        string?[] candidates =
        [
            ctx.User.Claims.FirstOrDefault(c => c.Type is "UserId" or "Jellyfin-UserId")?.Value,
            ctx.Request.Query["userId"].FirstOrDefault(),
            ctx.Request.RouteValues.TryGetValue("userId", out var route) ? route?.ToString() : null,
        ];

        foreach (var candidate in candidates)
        {
            if (Guid.TryParse(candidate, out userId) && userId != Guid.Empty)
                return true;
        }

        userId = Guid.Empty;
        return false;
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

public static class BaseItemExtensions
{
    public static bool IsGelato(this BaseItem item)
    {
        return !string.IsNullOrWhiteSpace(item.GetProviderId("Stremio"));
    }

    /// <summary>
    /// Drops the EndDate a Gelato metadata result carries when the refreshed item is not Gelato's
    /// own. <see cref="GelatoManager.IntoBaseItem"/> always sets EndDate, since the unreleased
    /// filter needs one, but on a native item that is metadata the library never had: a running
    /// series ends up with an end date, and an item whose release is unknown with the 9999
    /// sentinel. Jellyfin only fills an empty EndDate, so the value stays until a metadata replace.
    /// <paramref name="sourceProviderIds"/> are the ids of the item being refreshed.
    /// </summary>
    public static void KeepEndDateOnlyForGelato(
        this BaseItem item,
        IReadOnlyDictionary<string, string> sourceProviderIds
    )
    {
        if (
            !sourceProviderIds.TryGetValue("Stremio", out var stremioId)
            || string.IsNullOrWhiteSpace(stremioId)
        )
        {
            item.EndDate = null;
        }
    }

    public static bool HasStreamTag(this BaseItem item)
    {
        return item.Tags is not null
            && item.Tags.Contains(GelatoManager.StreamTag, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The movie/episode a stream row is a version of; any other item as it is.
    /// </summary>
    public static BaseItem PrimaryVersionOrSelf(
        this BaseItem item,
        ILibraryManager libraryManager
    ) =>
        item.HasStreamTag()
        && (item as Video)?.PrimaryVersionId is { } primaryId
        && libraryManager.GetItemById(primaryId) is { } primary
            ? primary
            : item;

    /// <summary>
    /// Whether Gelato serves this item: a stream row, or an item whose path is a <c>gelato://</c>
    /// placeholder.
    /// </summary>
    /// <remarks>
    /// A local file that only carries a Stremio id (EnableMixed) is not one of them and is left to
    /// Jellyfin.
    /// </remarks>
    public static bool IsGelatoPlaybackItem(this BaseItem item) =>
        item.HasStreamTag()
        || (item.Path?.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase) ?? false);

    public static bool IsPrimaryVersion(this BaseItem item)
    {
        return !item.HasStreamTag()
            && (item as Video)?.PrimaryVersionId is null
            && !item.IsVirtualItem;
    }

    public static bool IsStream(this BaseItem item)
    {
        return !string.IsNullOrWhiteSpace(item.GetProviderId("Stremio"))
            && !item.IsPrimaryVersion();
    }

    public static T? GelatoData<T>(this BaseItem item, string key)
    {
        if (string.IsNullOrEmpty(item.ExternalId))
            return default;

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.ExternalId);
            return dict != null && dict.TryGetValue(key, out var el)
                ? el.Deserialize<T>()
                : default;
        }
        catch
        {
            return default;
        }
    }

    public static void SetGelatoData<T>(this BaseItem item, string key, T value)
    {
        Dictionary<string, JsonElement> data;

        try
        {
            data = string.IsNullOrEmpty(item.ExternalId)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.ExternalId)
                    ?? new Dictionary<string, JsonElement>();
        }
        catch
        {
            data = new Dictionary<string, JsonElement>();
        }

        data[key] = JsonSerializer.SerializeToElement(value);
        item.ExternalId = JsonSerializer.Serialize(data);
    }

    private static readonly HashSet<string> SubtitleExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "vtt",
        "srt",
        "ass",
        "ssa",
        "sub",
        "idx",
        "smi",
    };

    /// <summary>
    /// The file name Gelato hands Jellyfin when it saves a subtitle: the release filename the addon
    /// sent, or <c>{id}.strm</c> when it sent none. A Gelato path is a URL or a <c>gelato://stub</c>,
    /// which Jellyfin would turn into a name nothing looks for afterwards.
    /// </summary>
    public static string GelatoSubtitlePathName(this BaseItem item)
    {
        var gelatoFilename = item.GelatoData<string>("filename");
        return string.IsNullOrEmpty(gelatoFilename) ? $"{item.Id:N}.strm" : gelatoFilename;
    }

    /// <summary>
    /// The part Jellyfin puts in front of <c>.{lang}.{ext}</c>, derived from
    /// <see cref="GelatoSubtitlePathName"/> the same way Jellyfin derives it from a media path.
    /// </summary>
    public static string GelatoSubtitleBaseName(this BaseItem item) =>
        Path.GetFileNameWithoutExtension(item.GelatoSubtitlePathName());

    /// <summary>
    /// Subtitle files saved for a Gelato item in its internal metadata folder, named
    /// <c>{base}.{lang}.{ext}</c> or <c>{base}.{lang}.{N}.{ext}</c>. Jellyfin never records them as
    /// media streams because the item has no local file, so Gelato has to look itself.
    /// </summary>
    public static IEnumerable<(string Path, string Language, string Codec)> GetGelatoSubtitleFiles(
        this BaseItem item
    )
    {
        var metaPath = item.GetInternalMetadataPath();
        if (!Directory.Exists(metaPath))
            yield break;

        var baseName = item.GelatoSubtitleBaseName();

        foreach (var file in Directory.EnumerateFiles(metaPath))
        {
            var fname = Path.GetFileName(file);

            // Must start with baseName + "."
            if (!fname.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                continue;

            var ext = Path.GetExtension(fname).TrimStart('.');
            if (!SubtitleExtensions.Contains(ext))
                continue;

            // Parse language from suffix: {baseName}.{lang}.{ext} or {baseName}.{lang}.{N}.{ext}
            var suffix = fname.Substring(baseName.Length + 1); // everything after "baseName."
            var parts = Path.GetFileNameWithoutExtension(suffix).Split('.');
            var langCode = parts.Length > 0 ? parts[0] : "und";

            yield return (file, langCode, ext.ToLowerInvariant());
        }
    }
}

public static class StringExtensions
{
    public static bool IsUrl(this string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

public static class MediaSourceExtensions
{
    /// <summary>
    /// Hands the client a stub on the File protocol instead of the stream's own path. The addon
    /// URL carries the debrid API key and names a host only Jellyfin has to reach, so no response
    /// a client can ask for may contain it; File and a path the client cannot open is what makes
    /// clients stream through Jellyfin, which resolves the real URL again. A placeholder
    /// (gelato://, stremio://) is stubbed the same way, there is no file behind it either. A real
    /// file path is left alone, so a version merged in by hand still direct-plays from the share.
    /// </summary>
    public static bool Stub(this MediaSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Path is not { Length: > 0 } path || !IsStubbed(path))
            return false;

        source.Path = "/stub";
        source.IsRemote = false;
        source.Protocol = MediaProtocol.File;
        return true;
    }

    private static bool IsStubbed(string path) =>
        path.IsUrl()
        || path.StartsWith("gelato", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("stremio", StringComparison.OrdinalIgnoreCase);
}
