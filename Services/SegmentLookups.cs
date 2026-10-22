using System.Globalization;
using System.Text.Json;
using Gelato.Providers;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;

namespace Gelato.Services;

/// <summary>
/// What the segment provider last found for a stream row: IntroDB's answer and whether the row's
/// chapters were looked at. Kept in Gelato's cache database, so it outlasts a restart.
/// </summary>
/// <remarks>
/// A row with an answer is not looked up again until it expires: never after a hit, after
/// <see cref="MissRetry"/> when IntroDB had nothing, after <see cref="FailureRetry"/> when it
/// could not be asked. A row without one (new, or looked up before this was kept) is looked up
/// on its next playback, whatever the state of its media info. So is a hit whose row has no
/// segments: a row's id is a hash of its URL, so a row deleted with its title and made again
/// gets the old row's answer, and Jellyfin deletes the segments without telling the provider.
/// </remarks>
public sealed class SegmentLookups(
    PalcoCacheService cache,
    Lazy<IMediaSegmentManager> mediaSegmentManager
)
{
    public const string Hit = "hit";
    public const string Miss = "miss";
    public const string Failed = "failed";

    /// <summary>No IntroDB lookup: a movie, whose chapters are all there is.</summary>
    public const string None = "none";

    private const string Namespace = "gelato:segments";

    public static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);
    public static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(10);

    /// <param name="Outcome">IntroDB's answer: <see cref="Hit"/>, <see cref="Miss"/>, <see cref="Failed"/> or <see cref="None"/>.</param>
    /// <param name="Types">The segment types IntroDB gave, kept when the row is not asked again.</param>
    /// <param name="Chapters">Whether the row's chapters were turned into segments.</param>
    public sealed record Lookup(string Outcome, MediaSegmentType[] Types, bool Chapters);

    private static bool RemuxDbEnabled =>
        GelatoPlugin.Instance?.Configuration.RemuxDbEnabled ?? false;

    /// <summary>Whether the provider reads the row's chapters: Gelato's stream rows, with RemuxDB on.</summary>
    public static bool UsesChapters(BaseItem row) =>
        RemuxDbEnabled && row is Video video && video.HasStreamTag();

    public Lookup? Get(Guid rowId)
    {
        var value = cache.Get(Key(rowId), Namespace);
        if (value is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<Lookup>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Set(Guid rowId, Lookup lookup)
    {
        var retry = lookup.Outcome switch
        {
            Miss => MissRetry,
            Failed => FailureRetry,
            _ => TimeSpan.Zero,
        };
        cache.Set(
            Key(rowId),
            JsonSerializer.Serialize(lookup),
            (int)retry.TotalSeconds,
            Namespace
        );
    }

    public void Forget(Guid rowId)
    {
        cache.Delete(Key(rowId), Namespace);
        cache.Delete(OthersKey(rowId), Namespace);
    }

    /// <summary>
    /// Whether a playback of the row should run the segment providers: the provider has
    /// something to do for it and has no answer for it that still holds. With the provider
    /// disabled in the row's library, its answer does not count: the library's other providers
    /// run, again after <see cref="MissRetry"/> or when the library's disabled providers change.
    /// </summary>
    public bool IsDue(BaseItem row, LibraryOptions libraryOptions)
    {
        var chapters = UsesChapters(row);
        if (row is not (Episode or Movie) && !chapters)
            return false;

        if (IsDisabled(libraryOptions))
            return cache.Get(OthersKey(row.Id), Namespace) != DisabledProviders(libraryOptions);

        return Get(row.Id) is not { } lookup
            || (chapters && !lookup.Chapters)
            || (lookup.Outcome == Hit && !mediaSegmentManager.Value.HasSegments(row.Id));
    }

    /// <summary>
    /// Whether the row lost what IntroDB gave for it: a hit, and none of its segment types left
    /// on the row. Asked again then.
    /// </summary>
    public static bool IsLost(Lookup lookup, IEnumerable<MediaSegmentDto> segments) =>
        lookup.Outcome == Hit && !segments.Any(s => lookup.Types.Contains(s.Type));

    /// <summary>
    /// Keeps that the segment providers ran for the row. Only needed with Gelato's provider
    /// disabled, which keeps its own answer otherwise.
    /// </summary>
    public void SetRan(Guid rowId, LibraryOptions libraryOptions)
    {
        if (IsDisabled(libraryOptions))
        {
            cache.Set(
                OthersKey(rowId),
                DisabledProviders(libraryOptions),
                (int)MissRetry.TotalSeconds,
                Namespace
            );
        }
    }

    private static bool IsDisabled(LibraryOptions libraryOptions) =>
        libraryOptions.DisabledMediaSegmentProviders.Contains(
            IntroDbSegmentProvider.ProviderName,
            StringComparer.OrdinalIgnoreCase
        );

    private static string DisabledProviders(LibraryOptions libraryOptions) =>
        string.Join(
            '|',
            libraryOptions
                .DisabledMediaSegmentProviders.Select(n => n.ToLowerInvariant())
                .Order(StringComparer.Ordinal)
        );

    private static string OthersKey(Guid rowId) => Key(rowId) + ":others";

    private static string Key(Guid rowId) => rowId.ToString("N", CultureInfo.InvariantCulture);
}
