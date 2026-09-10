using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

/// <summary>
/// Reattaches orphaned watch state, and re-imports the items it has nothing to attach to.
/// </summary>
/// <remarks>
/// Deleting an item parks its user data on a placeholder item instead of deleting it, so the data
/// survives. <see cref="GelatoManager"/> reattaches it whenever it creates an item, which covers
/// everything a catalog sync or a series tree sync brings back. It does not cover a movie that is
/// in no catalog — one added on demand by opening a search result — because nothing recreates it.
/// On a copy of a production library that was 209 of the 291 movies carrying watch state.
///
/// Those are still recoverable: the parked rows are keyed by imdb id, and Gelato item ids are a
/// deterministic hash of <c>gelato://stub/{imdbId}</c>, so a parked key alone is enough to rebuild
/// the item's identity and re-fetch it from the addon.
///
/// Run by hand, never on a schedule. A parked row records when its item was removed but never why,
/// so nothing in the data separates a library the upgrade destroyed from one somebody cleared out on
/// purpose. Inferring it from the shape of the removal was tried and thrown away: a multi-select
/// delete and a migration look alike. Starting the task is the statement of intent instead.
///
/// This is the only place in Gelato that reads the Jellyfin database directly. Detached user data
/// is not reachable through any public interface — <see cref="IUserDataManager"/> only reads by
/// item, and these rows have no item. The access is read-only: every write still goes through
/// <see cref="IItemPersistenceService"/> and the normal insert path.
/// </remarks>
public sealed partial class RepairWatchStateTask(
    ILogger<RepairWatchStateTask> log,
    ILibraryManager libraryManager,
    IItemPersistenceService persistence,
    IDbContextFactory<JellyfinDbContext> dbFactory,
    GelatoManager manager
) : IScheduledTask
{
    /// <summary>
    /// The well-known item Jellyfin parks detached user data on. Mirrors
    /// <c>BaseItemRepository.PlaceholderId</c>, which is not on the plugin-facing API surface.
    /// </summary>
    private static readonly Guid PlaceholderId = new("00000000-0000-0000-0000-000000000001");

    // Uneven because the phases are: reattaching is a local query per item and usually finds
    // nothing after an upgrade, while re-importing is a network round trip each.
    private const double ReattachShare = 10;
    private const double MovieShare = 65;
    private const double SeriesShare = 25;

    public string Name => "Repair watch state";
    public string Key => "GelatoRepairWatchState";

    public string Description =>
        "Run this if Gelato items disappeared and took your play positions, played flags and "
        + "favourites with them — after a Jellyfin 12 upgrade, for example. Re-imports the movies "
        + "and episodes that are missing and puts the watch state back on them. Anything you "
        + "deleted yourself will come back too, so only run it if you actually lost something.";

    public string Category => "Gelato Maintenance";

    // No schedule on purpose. A parked row records when an item was removed, never why, so nothing
    // in the data distinguishes a library the upgrade ate from one somebody deliberately cleared
    // out. Rather than guess, the run itself is the statement of intent: whoever starts this task
    // knows which of the two happened.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <summary>
    /// A bare imdb id, as a movie or series parks it.
    /// </summary>
    /// <remarks>
    /// The digit bound is what separates a film from an episode, and it is not cosmetic: an episode
    /// parks its series' key with a six digit season/episode suffix, so <c>tt9067020001003</c> also
    /// matches a naive <c>tt\d+</c>. Real imdb ids run to eight digits and an episode key is always
    /// at least seven plus six, so nine is a safe ceiling with four digits of headroom. Without it
    /// most candidates are episodes and the task asks the addon for films that do not exist.
    /// </remarks>
    [GeneratedRegex(@"^tt\d{7,9}$")]
    private static partial Regex ImdbKey();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var parked = await LoadParkedKeysAsync(ct).ConfigureAwait(false);
        if (parked.Keys.Count == 0)
        {
            log.LogDebug("RepairWatchState: no detached watch state, nothing to do");
            progress.Report(100);
            return;
        }

        log.LogInformation(
            "RepairWatchState: {Keys} detached key(s) hold watch state",
            parked.Keys.Count
        );

        var (reattached, alreadyHeld) = await ReattachExistingAsync(parked, progress, ct)
            .ConfigureAwait(false);

        var movies = await ResurrectMissingMoviesAsync(parked.Keys, progress, ct)
            .ConfigureAwait(false);
        var series = await ResurrectMissingEpisodesAsync(parked.Keys, progress, ct)
            .ConfigureAwait(false);

        log.LogInformation(
            "RepairWatchState: reattached {Reattached} existing item(s) ({AlreadyHeld} already held their state), re-imported {Movies} movie(s) and the episodes of {Series} series",
            reattached,
            alreadyHeld,
            movies,
            series
        );
        progress.Report(100);
    }

    /// <summary>
    /// The parked rows worth restoring: every key, and which users hold state under each.
    /// </summary>
    private sealed record ParkedState(HashSet<string> Keys, ILookup<string, Guid> UsersByKey);

    private async Task<ParkedState> LoadParkedKeysAsync(CancellationToken ct)
    {
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var rows = await db
                .UserData.AsNoTracking()
                .Where(e => e.ItemId == PlaceholderId)
                // A row holding nothing is not worth restoring, and re-importing an item just to
                // reattach an empty row would be re-adding it for no reason.
                .Where(e =>
                    e.Played
                    || e.PlayCount > 0
                    || e.PlaybackPositionTicks > 0
                    || e.IsFavorite
                    || e.Rating != null
                    || e.Likes != null
                )
                .Select(e => new { e.CustomDataKey, e.UserId })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return new ParkedState(
                rows.Select(r => r.CustomDataKey).ToHashSet(StringComparer.Ordinal),
                rows.ToLookup(r => r.CustomDataKey, r => r.UserId, StringComparer.Ordinal)
            );
        }
    }

    /// <summary>
    /// Phase one: hand parked rows back to items that are already in the library. Covers anyone who
    /// re-imported on a build that did not yet reattach on insert.
    /// </summary>
    /// <remarks>
    /// Jellyfin's reattach is a single update over every parked row matching the item's keys, so one
    /// row whose user and key the item already holds fails the whole item with a unique constraint
    /// violation. That is the normal state of a row parked long ago for an item that was re-added
    /// and watched again, so it is checked for here instead of being logged as a failure: an item
    /// that already holds every waiting row is skipped, and one that holds only some of them is
    /// reported without the exception, since nothing can be reattached for it either way.
    /// </remarks>
    private async Task<(int Reattached, int AlreadyHeld)> ReattachExistingAsync(
        ParkedState parked,
        IProgress<double> progress,
        CancellationToken ct
    )
    {
        var items = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes =
                    [
                        BaseItemKind.Movie,
                        BaseItemKind.Series,
                        BaseItemKind.Season,
                        BaseItemKind.Episode,
                    ],
                    Recursive = true,
                    HasAnyProviderId = new Dictionary<string, string>
                    {
                        { "Stremio", string.Empty },
                    },
                    GroupByPresentationUniqueKey = false,
                    GroupBySeriesPresentationUniqueKey = false,
                    CollapseBoxSetItems = false,
                    IsDeadPerson = true,
                }
            )
            // Stream rows copy the provider ids of the item they hang off, so they resolve to the
            // same keys. Reattaching onto one would hide the watch state behind a row nobody sees.
            .Where(item => !item.IsStream())
            .ToList();

        var reattached = 0;
        var alreadyHeld = 0;
        var processed = 0;

        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                // Only touch items that actually have something waiting, so a healthy library costs
                // one query rather than a transaction per item.
                var waiting = item
                    .GetUserDataKeys()
                    .SelectMany(key => parked.UsersByKey[key].Select(user => (user, key)))
                    .ToHashSet();

                if (waiting.Count > 0)
                {
                    var held = await db
                        .UserData.AsNoTracking()
                        .Where(e => e.ItemId == item.Id)
                        .Select(e => new { e.UserId, e.CustomDataKey })
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    var colliding = waiting.Count(w =>
                        held.Exists(h => h.UserId == w.user && h.CustomDataKey == w.key)
                    );

                    if (colliding == waiting.Count)
                    {
                        // What is on the item is newer than the parked row, so leaving it is right.
                        log.LogDebug(
                            "RepairWatchState: {Name} ({Id}) already holds the {Rows} parked row(s), leaving them",
                            item.Name,
                            item.Id,
                            waiting.Count
                        );
                        alreadyHeld++;
                    }
                    else if (colliding > 0)
                    {
                        log.LogWarning(
                            "RepairWatchState: cannot reattach {Name} ({Id}): {Colliding} of {Rows} parked row(s) collide with rows the item already holds, and Jellyfin reattaches all or nothing",
                            item.Name,
                            item.Id,
                            colliding,
                            waiting.Count
                        );
                    }
                    else
                    {
                        try
                        {
                            await persistence.ReattachUserDataAsync(item, ct).ConfigureAwait(false);
                            reattached++;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            log.LogWarning(
                                ex,
                                "RepairWatchState: could not reattach {Name} ({Id})",
                                item.Name,
                                item.Id
                            );
                        }
                    }
                }

                if (++processed % 50 == 0)
                    progress.Report(processed * ReattachShare / items.Count);
            }
        }

        progress.Report(ReattachShare);
        return (reattached, alreadyHeld);
    }

    /// <summary>
    /// Phase two: re-import movies that hold parked watch state and that nothing else brings back.
    /// The insert path reattaches their watch state as it creates them.
    /// </summary>
    private async Task<int> ResurrectMissingMoviesAsync(
        HashSet<string> parkedKeys,
        IProgress<double> progress,
        CancellationToken ct
    )
    {
        var imdbKeys = parkedKeys.Where(k => ImdbKey().IsMatch(k)).ToList();
        if (imdbKeys.Count == 0)
        {
            return 0;
        }

        var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
        if (cfg.Stremio is not { } stremio)
        {
            log.LogWarning("RepairWatchState: Gelato URL is not configured, cannot re-import");
            return 0;
        }

        if (manager.TryGetMovieFolder(cfg) is not { } movieFolder)
        {
            log.LogWarning("RepairWatchState: movie library folder is missing, cannot re-import");
            return 0;
        }

        // Anything still in the library was handled in phase one.
        var missing = imdbKeys
            .Where(key =>
                libraryManager.GetItemById(
                    libraryManager.GetNewItemId($"gelato://stub/{key}", typeof(Movie))
                ) is null
            )
            .ToList();

        log.LogInformation(
            "RepairWatchState: {Missing} of {Total} movie(s) with parked watch state are gone from the library, re-importing",
            missing.Count,
            imdbKeys.Count
        );

        var resurrected = 0;
        var processed = 0;

        await Parallel
            .ForEachAsync(
                missing,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (key, innerCt) =>
                {
                    try
                    {
                        var meta = await stremio
                            .GetMetaAsync(key, StremioMediaType.Movie)
                            .ConfigureAwait(false);

                        if (meta is null)
                        {
                            log.LogDebug("RepairWatchState: addon returned no meta for {Key}", key);
                        }
                        else if (
                            !string.Equals(meta.ImdbId, key, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(meta.Id, key, StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            // The item would be created under an id derived from what the addon
                            // returned, not from the key we are repairing, so the watch state would
                            // not find it anyway. Better to leave the library alone.
                            log.LogDebug(
                                "RepairWatchState: addon answered {Key} with {MetaId}, skipping",
                                key,
                                meta.Id
                            );
                        }
                        else
                        {
                            meta.Type = StremioMediaType.Movie;
                            var (item, _) = await manager
                                // Queue the refresh: starting a few hundred at once drowns the
                                // addon and slows the imports still running.
                                .InsertMeta(movieFolder, meta, null, true, true, true, innerCt)
                                .ConfigureAwait(false);

                            if (item is not null)
                                Interlocked.Increment(ref resurrected);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "RepairWatchState: failed to re-import {Key}", key);
                    }

                    var done = Interlocked.Increment(ref processed);
                    progress.Report(ReattachShare + done * MovieShare / missing.Count);
                }
            )
            .ConfigureAwait(false);

        return resurrected;
    }

    /// <summary>
    /// Phase three: refill the episode trees of series whose episodes hold parked watch state.
    /// </summary>
    /// <remarks>
    /// A series is a folder, so the upgrade migration leaves it alone and only its episodes are
    /// taken. Nothing puts them back on its own: the startup sync only walks series that are still
    /// <see cref="SeriesStatus.Continuing"/>, which on a real library was 4 of 51, leaving the other
    /// 47 shows as empty shells with their watch state parked indefinitely.
    ///
    /// The affected series are found by matching rather than by parsing. An episode parks its
    /// series' key with a six digit season/episode suffix, and the series itself is still here to
    /// ask for its keys, so a parked key that is one of those keys plus six digits identifies the
    /// series it belongs to — no guessing at where an imdb id ends and a season number begins.
    /// </remarks>
    private async Task<int> ResurrectMissingEpisodesAsync(
        HashSet<string> parkedKeys,
        IProgress<double> progress,
        CancellationToken ct
    )
    {
        if (parkedKeys.Count == 0)
        {
            return 0;
        }

        var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
        if (cfg.Stremio is not { } stremio)
        {
            return 0;
        }

        var orphaned = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Series],
                    Recursive = true,
                    HasAnyProviderId = new Dictionary<string, string>
                    {
                        { "Stremio", string.Empty },
                    },
                    IsDeadPerson = true,
                }
            )
            .OfType<Series>()
            .Where(s => s.GetUserDataKeys().Any(k => parkedKeys.Any(p => IsEpisodeKeyOf(p, k))))
            .ToList();

        if (orphaned.Count == 0)
        {
            return 0;
        }

        log.LogInformation(
            "RepairWatchState: {Count} series have episodes with parked watch state missing, refilling their trees",
            orphaned.Count
        );

        var repaired = 0;
        var processed = 0;

        foreach (var series in orphaned)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
                if (meta is null)
                {
                    log.LogDebug(
                        "RepairWatchState: addon returned no meta for series {Name}",
                        series.Name
                    );
                }
                else
                {
                    // Sync the tree directly rather than through the scheduled sync, which only
                    // considers continuing series and would skip most of these.
                    await manager
                        .SyncSeriesTreesAsync(cfg, meta, ct, existingSeries: series)
                        .ConfigureAwait(false);
                    repaired++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(
                    ex,
                    "RepairWatchState: failed to refill {Name} ({Id})",
                    series.Name,
                    series.Id
                );
            }

            progress.Report(
                ReattachShare + MovieShare + (++processed * SeriesShare / orphaned.Count)
            );
        }

        return repaired;
    }

    /// <summary>
    /// Whether <paramref name="parkedKey"/> is <paramref name="seriesKey"/> with a season and
    /// episode number appended, and so belongs to an episode of that series.
    /// </summary>
    private static bool IsEpisodeKeyOf(string parkedKey, string seriesKey)
    {
        const int SuffixLength = 6;

        return parkedKey.Length == seriesKey.Length + SuffixLength
            && parkedKey.StartsWith(seriesKey, StringComparison.Ordinal)
            && parkedKey.AsSpan(seriesKey.Length).ContainsOnlyAsciiDigits();
    }
}

internal static class SpanExtensions
{
    public static bool ContainsOnlyAsciiDigits(this ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
