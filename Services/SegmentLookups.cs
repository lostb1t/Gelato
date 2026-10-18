using System.Globalization;
using System.Text.Json;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Gelato.Services;

/// <summary>
/// What the segment provider last found for a stream row: IntroDB's answer and whether the row's
/// chapters were looked at. Kept in Gelato's cache database, so it outlasts a restart.
/// </summary>
/// <remarks>
/// A row with an answer is not looked up again until it expires: never after a hit, after
/// <see cref="MissRetry"/> when IntroDB had nothing, after <see cref="FailureRetry"/> when it
/// could not be asked. A row without one (new, or looked up before this was kept) is looked up
/// on its next playback, whatever the state of its media info.
/// </remarks>
public sealed class SegmentLookups(PalcoCacheService cache)
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

    public void Forget(Guid rowId) => cache.Delete(Key(rowId), Namespace);

    /// <summary>
    /// Whether a playback of the row should run the segment providers: the provider has
    /// something to do for it and has no answer for it that still holds.
    /// </summary>
    public bool IsDue(BaseItem row)
    {
        var chapters = UsesChapters(row);
        if (row is not (Episode or Movie) && !chapters)
            return false;

        return Get(row.Id) is not { } lookup || (chapters && !lookup.Chapters);
    }

    private static string Key(Guid rowId) => rowId.ToString("N", CultureInfo.InvariantCulture);
}
