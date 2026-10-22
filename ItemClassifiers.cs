using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Gelato;

/// <summary>
/// What an item, a DTO or a path is to Gelato. Every such test is here, each named for what it
/// accepts: where two look alike, their remarks say how they differ.
/// </summary>
public static class ItemClassifiers
{
    /// <summary>
    /// Whether the item carries a Stremio id: Gelato's own items (titles, seasons, episodes,
    /// stream rows) and a local file matched to one (EnableMixed).
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="IsGelatoPlaybackItem"/>, which leaves the local files out.
    /// </remarks>
    public static bool HasStremioId(this BaseItem item)
    {
        return !string.IsNullOrWhiteSpace(item.GetProviderId("Stremio"));
    }

    /// <summary>
    /// Whether the item is a stream row: one stream of a movie/episode, saved as a version of it
    /// and tagged <see cref="GelatoManager.StreamTag"/>.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="IsStremioNonPrimary"/>, which also takes any other version or
    /// virtual item with a Stremio id.
    /// </remarks>
    public static bool IsStreamRow(this BaseItem item)
    {
        return item.Tags is not null
            && item.Tags.Contains(GelatoManager.StreamTag, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether Gelato serves this item: a stream row, or an item whose path is a <c>gelato://</c>
    /// placeholder.
    /// </summary>
    /// <remarks>
    /// A local file that only carries a Stremio id (EnableMixed) is not one of them and is left to
    /// Jellyfin.
    /// </remarks>
    public static bool IsGelatoPlaybackItem(this BaseItem item) =>
        item.IsStreamRow() || item.Path.IsGelatoUri();

    public static bool IsPrimaryVersion(this BaseItem item)
    {
        return !item.IsStreamRow()
            && (item as Video)?.PrimaryVersionId is null
            && !item.IsVirtualItem;
    }

    /// <summary>
    /// Whether the item has a Stremio id and is not a primary version: a stream row, another
    /// version linked to a movie/episode, or a virtual item.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="IsStreamRow"/>, which takes the tagged stream rows only.
    /// </remarks>
    public static bool IsStremioNonPrimary(this BaseItem item)
    {
        return item.HasStremioId() && !item.IsPrimaryVersion();
    }

    /// <summary>
    /// Whether a DTO is a remote movie, episode, series or season: how a Gelato title looks once it
    /// is a DTO, where its item may not be at hand.
    /// </summary>
    public static bool IsRemoteTitle(this BaseItemDto dto)
    {
        return dto.LocationType == LocationType.Remote
            && (
                dto.Type == BaseItemKind.Movie
                || dto.Type == BaseItemKind.Episode
                || dto.Type == BaseItemKind.Series
                || dto.Type == BaseItemKind.Season
            );
    }

    /// <summary>Whether the path is a <c>gelato://</c> URI: a placeholder or a stub.</summary>
    public static bool IsGelatoUri(this string? path) =>
        path?.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// Whether the path starts with <c>gelato</c> in any form, <c>gelato://</c> or not. Wider than
    /// <see cref="IsGelatoUri"/>; throws on null as <see cref="string.StartsWith(string)"/> does.
    /// </summary>
    public static bool HasGelatoPrefix(this string path) =>
        path.StartsWith("gelato", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the path is one Gelato puts where a stream would be: <see cref="HasGelatoPrefix"/>
    /// or a <c>stremio</c> path.
    /// </summary>
    public static bool IsPlaceholderPath(this string path) =>
        path.HasGelatoPrefix() || path.StartsWith("stremio", StringComparison.OrdinalIgnoreCase);
}
