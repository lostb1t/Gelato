using System.Text.Json.Serialization;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Gelato.Controllers;

/// <summary>
/// The target of Seerr's (Jellyseerr, Overseerr) notification webhook: an approved request puts
/// the title into the library, as opening it in a search would. Seerr's own Jellyfin scan then
/// finds it and marks the request available. Seerr sends the Jellyfin API key in its
/// Authorization header field (<c>MediaBrowser Token="..."</c>).
/// </summary>
[ApiController]
[Route("gelato/seerr")]
[Authorize(Policy = Policies.RequiresElevation)]
public class SeerrController(
    GelatoManager manager,
    IUserManager userManager,
    ILogger<SeerrController> log
) : ControllerBase
{
    private static readonly HashSet<string> Approved = new(StringComparer.OrdinalIgnoreCase)
    {
        "MEDIA_APPROVED",
        "MEDIA_AUTO_APPROVED",
    };

    /// <summary>
    /// Every other notification, Seerr's test among them, is answered 200 and does nothing: one
    /// webhook carries all the types the user ticked.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<object>> Notify([FromBody] SeerrNotification notification)
    {
        if (
            notification.NotificationType is not { } type
            || !Approved.Contains(type)
            || notification.Media is not { } media
        )
            return Ok(new { Status = "ignored" });

        var mediaType = media.MediaType?.ToLowerInvariant() switch
        {
            "movie" => StremioMediaType.Movie,
            "tv" => StremioMediaType.Series,
            _ => StremioMediaType.Unknown,
        };
        var ids = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(media.TmdbId))
            ids[nameof(MediaBrowser.Model.Entities.MetadataProvider.Tmdb)] = media.TmdbId.Trim();
        if (!string.IsNullOrWhiteSpace(media.TvdbId))
            ids[nameof(MediaBrowser.Model.Entities.MetadataProvider.Tvdb)] = media.TvdbId.Trim();
        if (mediaType is StremioMediaType.Unknown || ids.Count == 0)
            return BadRequest("media_type must be movie or tv, with a tmdbId or tvdbId");

        // The requester's own settings (folders, addon) when Seerr's user is a Jellyfin user: by
        // the Jellyfin user id a template may add ({{requestedBy_jellyfinUserId}}), else by name,
        // which the default template sends and which is the Jellyfin username for users Seerr
        // imported from Jellyfin. Anyone else gets the server's.
        var request = notification.Request;
        var user =
            (
                Guid.TryParse(request?.RequestedByJellyfinUserId, out var jellyfinId)
                    ? userManager.GetUserById(jellyfinId)
                    : null
            )
            ?? (
                request?.RequestedByUsername is { Length: > 0 } username
                    ? userManager.GetUserByName(username)
                    : null
            );
        var userId = user?.Id ?? Guid.Empty;
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);

        var root =
            mediaType is StremioMediaType.Series
                ? manager.TryGetSeriesFolder(userId)
                : manager.TryGetMovieFolder(userId);
        if (root is null)
            return BadRequest($"No {mediaType} folder configured for {user?.Username ?? "the server"}");

        if (cfg.Stremio is not { } stremio)
            return BadRequest("No addon configured");

        var meta = await GetMetaAsync(stremio, ids, mediaType).ConfigureAwait(false);

        // A meta provider that serves tt ids only (Cinemeta) knows nothing under Seerr's ids:
        // the IMDb id comes from TMDB, and the meta is asked for again with it.
        if (
            meta is null
            && ids.TryGetValue(nameof(MediaBrowser.Model.Entities.MetadataProvider.Tmdb), out var tmdb)
            && await stremio
                .GetImdbIdFromTmdbAsync(tmdb, mediaType, HttpContext.RequestAborted)
                .ConfigureAwait(false)
                is { } imdb
        )
        {
            log.LogInformation(
                "Seerr: no meta under {Ids}, asking with {ImdbId} from TMDB",
                string.Join(", ", ids.Select(i => $"{i.Key}={i.Value}")),
                imdb
            );
            ids[nameof(MediaBrowser.Model.Entities.MetadataProvider.Imdb)] = imdb;
            meta = await GetMetaAsync(stremio, ids, mediaType).ConfigureAwait(false);
        }

        if (meta is null)
        {
            log.LogWarning(
                "Seerr: the addon has no {Type} meta for {Ids}",
                mediaType,
                string.Join(", ", ids.Select(i => $"{i.Key}={i.Value}"))
            );
            return NotFound();
        }

        // A series gets all its seasons, whichever Seerr asked for: Gelato fetches the episodes as
        // it does for any series.
        var (item, created) = await manager
            .InsertMeta(
                root,
                meta,
                user,
                false,
                true,
                mediaType is StremioMediaType.Series,
                HttpContext.RequestAborted
            )
            .ConfigureAwait(false);
        if (item is null)
            return NotFound();

        if (created)
        {
            log.LogInformation("Seerr: inserted {Name} requested in Seerr", item.Name);

            // Seerr counts a movie as available only once Jellyfin lists a video stream for it, and
            // Seerr reads the library with an API key, which never syncs a title's streams: the
            // request stayed "processing" until someone opened the movie in Jellyfin.
            if (mediaType is StremioMediaType.Movie)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await manager
                            .SyncStreams(item, userId, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Seerr: stream sync failed for {Name}", item.Name);
                    }
                });
        }

        return Ok(new { Status = created ? "added" : "exists", Id = item.Id });
    }
    /// <summary>
    /// The meta under any of the ids, or null: an addon that cannot serve the last id it is
    /// asked for throws, and that is a title it does not have.
    /// </summary>
    private async Task<StremioMeta?> GetMetaAsync(
        GelatoStremioProvider stremio,
        Dictionary<string, string> ids,
        StremioMediaType mediaType
    )
    {
        try
        {
            return await stremio.GetMetaAsync(ids, mediaType).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Seerr: meta lookup failed for {Type}", mediaType);
            return null;
        }
    }
}

/// <summary>The fields of Seerr's default webhook JSON that Gelato reads.</summary>
public class SeerrNotification
{
    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; set; }

    [JsonPropertyName("media")]
    public SeerrMedia? Media { get; set; }

    [JsonPropertyName("request")]
    public SeerrRequest? Request { get; set; }
}

public class SeerrMedia
{
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("tmdbId")]
    public string? TmdbId { get; set; }

    [JsonPropertyName("tvdbId")]
    public string? TvdbId { get; set; }
}

public class SeerrRequest
{
    [JsonPropertyName("requestedBy_username")]
    public string? RequestedByUsername { get; set; }

    [JsonPropertyName("requestedBy_jellyfinUserId")]
    public string? RequestedByJellyfinUserId { get; set; }
}
