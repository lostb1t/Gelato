using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// A user's parental control (Dashboard > Users > Parental control) applied to titles the
/// library does not hold: the addon's search results, a person's titles from TMDB, and a result
/// that is about to be put into the library. Jellyfin hides a library item above the user's
/// maximum rating, without a rating where unrated items of its kind are blocked, with a blocked
/// tag or without an allowed one; a title it has not got was offered whatever its rating, and
/// opening it put it into the library.
/// </summary>
/// <remarks>
/// Judged by Jellyfin's own rule (<see cref="BaseItem.IsParentalAllowed"/>) on a stand-in of the
/// title in the folder it would go into: the folder's metadata country picks the rating system,
/// its tags are what the title inherits. The rating is TMDB's for that country, as Jellyfin's
/// TMDB provider writes it once the title is in, where Gelato already has it; else the addon's
/// certification; else TMDB is asked, only for a user whose settings need a rating.
/// </remarks>
public sealed class ParentalRatings(ILogger<ParentalRatings> log)
{
    /// <summary>How long a list of titles waits for TMDB's ratings.</summary>
    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(3);

    /// <summary>Whether any of the user's parental settings can hide a movie or a series.</summary>
    public static bool Restricts(User? user) =>
        user is not null
        && (
            NeedsRating(user)
            || user.GetPreference(PreferenceKind.BlockedTags).Length > 0
            || user.GetPreference(PreferenceKind.AllowedTags).Length > 0
        );

    /// <summary>Whether the user's parental settings judge a title by its rating.</summary>
    public static bool NeedsRating(User user) =>
        user.MaxParentalRatingScore.HasValue
        || user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems)
            is { Length: > 0 } blocked
            && (blocked.Contains(UnratedItem.Movie) || blocked.Contains(UnratedItem.Series));

    /// <summary>
    /// The titles of <paramref name="metas"/> the user may see, in their order. TMDB is asked,
    /// within <see cref="LookupBudget"/>, for the ratings neither the addon nor an earlier lookup
    /// gave; a title whose lookup did not answer in time is left out for a user whose settings
    /// need its rating, as the unreleased filter leaves out a movie whose dates did not come.
    /// <paramref name="budget"/> replaces the <see cref="LookupBudget"/>: zero asks TMDB nothing
    /// and leaves out every title whose rating is not known yet.
    /// </summary>
    public async Task<List<StremioMeta>> FilterAsync(
        IReadOnlyList<StremioMeta> metas,
        User? user,
        Func<StremioMediaType, Folder?> folderOf,
        GelatoStremioProvider stremio,
        CancellationToken ct,
        TimeSpan? budget = null
    )
    {
        if (!Restricts(user) || metas.Count == 0)
            return [.. metas];

        var unanswered = new HashSet<StremioMeta>(ReferenceEqualityComparer.Instance);
        if (NeedsRating(user!))
        {
            var lookups = metas.Where(NeedsLookup).ToList();
            if (lookups.Count > 0 && budget == TimeSpan.Zero)
            {
                unanswered.UnionWith(lookups);
            }
            else if (lookups.Count > 0)
            {
                // The lookups swallow their own failures, a cancelled request's too: one counts
                // as answered when it ended before the budget did.
                var answered = new System.Collections.Concurrent.ConcurrentDictionary<
                    StremioMeta,
                    bool
                >(ReferenceEqualityComparer.Instance);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(budget ?? LookupBudget);
                try
                {
                    await Parallel
                        .ForEachAsync(
                            lookups,
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = 4,
                                CancellationToken = cts.Token,
                            },
                            async (meta, t) =>
                            {
                                await LookupAsync(meta, stremio, t).ConfigureAwait(false);
                                if (!t.IsCancellationRequested)
                                    answered[meta] = true;
                            }
                        )
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    log.LogDebug("TMDB rating lookups ran out of time");
                }

                unanswered.UnionWith(lookups.Where(m => !answered.ContainsKey(m)));
            }
        }

        var kept = new List<StremioMeta>(metas.Count);
        foreach (var meta in metas)
        {
            if (unanswered.Contains(meta))
            {
                log.LogDebug(
                    "Parental control of {User}: {Name} ({Id}) left out, TMDB gave no rating in time",
                    user!.Username,
                    meta.GetName(),
                    meta.Id
                );
            }
            else if (Allows(meta, folderOf(meta.Type), user!, out var rating))
            {
                kept.Add(meta);
            }
            else
            {
                log.LogDebug(
                    "Parental control of {User}: {Name} ({Id}) left out, rated {Rating}",
                    user!.Username,
                    meta.GetName(),
                    meta.Id,
                    rating ?? "unrated"
                );
            }
        }

        return kept;
    }

    /// <summary>
    /// Whether the user may see the title in <paramref name="folder"/>, its rating asked from
    /// TMDB first where it is needed and missing. For a title that is about to be put into the
    /// library: no budget but TMDB's own timeout.
    /// </summary>
    public async Task<bool> AllowsAsync(
        StremioMeta meta,
        Folder? folder,
        User user,
        GelatoStremioProvider stremio,
        CancellationToken ct
    )
    {
        if (!Restricts(user))
            return true;

        if (NeedsRating(user) && NeedsLookup(meta))
            await LookupAsync(meta, stremio, ct).ConfigureAwait(false);

        return Allows(meta, folder, user, out _);
    }

    /// <summary>Whether the title's rating has to be asked from TMDB first.</summary>
    public static bool NeedsLookup(StremioMeta meta) =>
        string.IsNullOrWhiteSpace(meta.App_Extras?.Certification)
        && (
            meta.Type == StremioMediaType.Movie
                ? meta.App_Extras?.ReleaseDates is null
                : meta.App_Extras?.ContentRatings is null
        );

    /// <summary>Asks TMDB for the title's rating (<see cref="NeedsLookup"/>).</summary>
    public static Task LookupAsync(
        StremioMeta meta,
        GelatoStremioProvider stremio,
        CancellationToken ct
    ) =>
        meta.Type == StremioMediaType.Movie
            ? stremio.EnrichDigitalReleaseDateAsync(meta, ct)
            : stremio.EnrichContentRatingsAsync(meta, ct);

    private static bool Allows(StremioMeta meta, Folder? folder, User user, out string? rating)
    {
        BaseItem probe = meta.Type == StremioMediaType.Series ? new Series() : new Movie();
        probe.Name = meta.GetName();
        probe.ParentId = folder?.Id ?? Guid.Empty;
        probe.OfficialRating = rating = GetRating(meta, probe.GetPreferredMetadataCountryCode());
        return probe.IsParentalAllowed(user, false);
    }

    /// <summary>
    /// The title's rating as Jellyfin's TMDB provider writes it for the metadata country: the
    /// country's own (FSK-16, GB-15), else the US one (R, TV-MA), else, for a series, the first
    /// TMDB lists. Without TMDB's lists, the addon's certification.
    /// </summary>
    internal static string? GetRating(StremioMeta meta, string? country)
    {
        var ratings =
            meta.Type == StremioMediaType.Movie
                ? meta.App_Extras?.ReleaseDates?.Results?.Select(c =>
                    (
                        Country: c.Iso31661,
                        Rating: c
                            .ReleaseDates?.Where(d => !string.IsNullOrWhiteSpace(d.Certification))
                            // The theatrical release's, as TMDB's own country summary has it.
                            .OrderBy(d => d.Type == 3 ? 0 : 1)
                            .Select(d => d.Certification)
                            .FirstOrDefault()
                    )
                )
                : meta.App_Extras?.ContentRatings?.Select(c => (Country: c.Iso31661, c.Rating));
        var known = ratings?.Where(r => !string.IsNullOrWhiteSpace(r.Rating)).ToList() ?? [];

        if (
            !string.IsNullOrEmpty(country)
            && known.FirstOrDefault(r => string.Equals(r.Country, country, StringComparison.OrdinalIgnoreCase))
                is { Rating: { } own }
        )
        {
            return string.Equals(country, "US", StringComparison.OrdinalIgnoreCase)
                ? own
                : (country + "-" + own).Replace("DE-", "FSK-", StringComparison.OrdinalIgnoreCase);
        }

        if (known.FirstOrDefault(r => string.Equals(r.Country, "US", StringComparison.OrdinalIgnoreCase)) is { Rating: { } us })
            return us;

        if (meta.Type == StremioMediaType.Series && known.Count > 0)
            return known[0].Rating;

        return string.IsNullOrWhiteSpace(meta.App_Extras?.Certification)
            ? null
            : meta.App_Extras.Certification;
    }
}
