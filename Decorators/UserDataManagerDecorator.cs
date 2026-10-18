using Gelato.Services;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

/// <summary>
/// Hides the watch state of stream rows from everything that listens to
/// <see cref="IUserDataManager.UserDataSaved"/>: Jellyfin's own change notifier, Trakt, Webhook,
/// Playback Reporting and the like.
/// </summary>
/// <remarks>
/// Rows are alternate versions of their movie/episode, and Jellyfin marks every version played
/// along with the one that was watched (<c>Video.PropagatePlayedState</c>). A movie with ten
/// streams raised twelve saves per finished playback and eleven per played toggle, one per
/// version. The row's state is an internal copy (<see cref="Services.StreamUserDataSync"/> keeps
/// the movie's, and every version shows the movie's), so its saves are not forwarded; listeners
/// see the movie's saves only, like those of a movie without versions. Gelato's own listeners
/// use <see cref="ItemSaved"/>, which carries every save.
/// </remarks>
public sealed class UserDataManagerDecorator : IUserDataManager
{
    private readonly IUserDataManager _inner;

    [ThreadStatic]
    private static int _quiet;

    private const int Unchecked = 0;
    private const int Checked = 1;
    private const int Off = 2;

    // Listings compared at most before the shortcut is trusted without one that had state in it.
    private const int MaxResumeChecks = 20;

    private readonly ItemIdLookup _idLookup;
    private readonly Lazy<ILibraryManager> _libraryManager;
    private readonly ILogger<UserDataManagerDecorator> _log;
    private int _resumeShortcut = Unchecked;
    private int _resumeChecks;

    public UserDataManagerDecorator(
        IUserDataManager inner,
        ItemIdLookup idLookup,
        Lazy<ILibraryManager> libraryManager,
        ILogger<UserDataManagerDecorator> log
    )
    {
        _inner = inner;
        _idLookup = idLookup;
        _libraryManager = libraryManager;
        _log = log;
        _inner.UserDataSaved += OnInnerSaved;
    }

    /// <summary>
    /// Every save, stream rows included, before the filtering.
    /// </summary>
    public event EventHandler<UserDataSaveEventArgs>? ItemSaved;

    /// <summary>
    /// The saves other listeners get: everything but stream rows and Gelato's bookkeeping saves.
    /// </summary>
    public event EventHandler<UserDataSaveEventArgs>? UserDataSaved;

    /// <summary>
    /// Saves made while the returned scope is open are not forwarded to other listeners. For
    /// copies and repairs of state that was already announced. Per thread, like the saves.
    /// </summary>
    public IDisposable Quiet()
    {
        _quiet++;
        return new QuietScope();
    }

    private void OnInnerSaved(object? sender, UserDataSaveEventArgs e)
    {
        ItemSaved?.Invoke(this, e);

        if (_quiet > 0 || e.Item?.HasStreamTag() == true)
            return;

        UserDataSaved?.Invoke(this, e);
    }

    private sealed class QuietScope : IDisposable
    {
        public void Dispose() => _quiet--;
    }

    public void SaveUserData(
        User user,
        BaseItem item,
        UserItemData userData,
        UserDataSaveReason reason,
        CancellationToken cancellationToken
    )
    {
        if (
            item is Video row
            && row.HasStreamTag()
            && _libraryManager.Value.GetItemById(row.Id) is null
        )
        {
            SaveForDeletedRow(user, row, userData, reason, cancellationToken);
            return;
        }

        _inner.SaveUserData(user, item, userData, reason, cancellationToken);
    }

    /// <summary>
    /// A playback report for a stream row that a sync deleted while it played. The session still
    /// holds the row, and its save failed on the row's foreign key: every report answered 500 and
    /// the position was lost. The movie/episode holds the shared state anyway
    /// (<see cref="Services.StreamUserDataSync"/>), so the report goes there.
    /// </summary>
    private void SaveForDeletedRow(
        User user,
        Video row,
        UserItemData source,
        UserDataSaveReason reason,
        CancellationToken cancellationToken
    )
    {
        if (
            reason
                is not (
                    UserDataSaveReason.PlaybackStart
                    or UserDataSaveReason.PlaybackProgress
                    or UserDataSaveReason.PlaybackFinished
                )
            || FindPrimary(row) is not { } primary
            || _inner.GetUserData(user, primary) is not { } data
        )
        {
            _log.LogDebug("Not saving {Reason} for deleted stream {Id}", reason, row.Id);
            return;
        }

        data.LastPlayedDate = source.LastPlayedDate ?? data.LastPlayedDate;
        if (reason is not UserDataSaveReason.PlaybackStart)
        {
            data.PlaybackPositionTicks = source.PlaybackPositionTicks;
        }

        if (source.Played && !data.Played)
        {
            data.Played = true;
            data.PlayCount++;
        }

        _log.LogDebug(
            "Saving {Reason} for deleted stream {Id} on {PrimaryId}",
            reason,
            row.Id,
            primary.Id
        );
        _inner.SaveUserData(user, primary, data, reason, cancellationToken);
    }

    // A row deleted by a sync was unlinked first, so its owner is found by the title's id.
    private Video? FindPrimary(Video row)
    {
        var library = _libraryManager.Value;
        if (row.PrimaryVersionId is { } primaryId && library.GetItemById(primaryId) is Video owner)
            return owner;

        if (row.GetProviderId("Stremio") is not { Length: > 0 } stremioId)
            return null;

        var titles = _idLookup
            .WithProviderId("Stremio", stremioId)
            .Select(library.GetItemById)
            .OfType<Video>()
            .Where(v => !v.HasStreamTag() && v.GetBaseItemKind() == row.GetBaseItemKind())
            .ToList();
        return titles.FirstOrDefault(v => v.ParentId == row.ParentId) ?? titles.FirstOrDefault();
    }

    public void SaveUserData(
        User user,
        BaseItem item,
        UpdateUserItemDataDto userDataDto,
        UserDataSaveReason reason
    ) => _inner.SaveUserData(user, item, userDataDto, reason);

    public UserItemData? GetUserData(User user, BaseItem item) => _inner.GetUserData(user, item);

    public UserItemDataDto? GetUserDataDto(BaseItem item, User user) =>
        _inner.GetUserDataDto(item, user);

    public Dictionary<Guid, UserItemData> GetUserDataBatch(
        IReadOnlyList<BaseItem> items,
        User user
    ) => _inner.GetUserDataBatch(items, user);

    public VersionResumeData? GetResumeUserData(User user, BaseItem item) =>
        _inner.GetResumeUserData(user, item);

    /// <summary>
    /// The most recently played version of each item, which Jellyfin shows the item's state from.
    /// </summary>
    /// <remarks>
    /// Jellyfin loads every version of every title on the page for it and asks each for its local
    /// versions: 2 N + 2 commands for a title with N stream rows, 930 for a listing of 836 movies
    /// of which 47 had been opened. Two queries say which titles have stream rows alone and which
    /// of those rows hold any state of this user. Rows without state cannot be the answer, so only
    /// the others are loaded; the pick among them is Jellyfin's. Everything else goes to Jellyfin.
    /// <para>
    /// Which versions Jellyfin compares, and in which order, is copied from it and can change with
    /// a Jellyfin release. So after every start the first listings are answered both ways, until
    /// one with watched stream rows in it agreed (or <see cref="MaxResumeChecks"/> did). A
    /// difference turns the shortcut off until the next start and Jellyfin answers alone.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<Guid, VersionResumeData> GetResumeUserDataBatch(
        IReadOnlyList<BaseItem> items,
        User user
    )
    {
        var state = Volatile.Read(ref _resumeShortcut);
        if (state == Off)
        {
            return _inner.GetResumeUserDataBatch(items, user);
        }

        var result = ResumeWithShortcut(items, user, out var streamTitles);
        if (state == Checked || streamTitles.Count == 0)
        {
            return result;
        }

        var expected = _inner.GetResumeUserDataBatch(items, user);
        if (
            expected.Count != result.Count
            || expected.Any(e =>
                !result.TryGetValue(e.Key, out var mine) || mine.VersionId != e.Value.VersionId
            )
        )
        {
            Volatile.Write(ref _resumeShortcut, Off);
            _log.LogWarning(
                "Resume state of {Count} items differs from Jellyfin's ({Mine} against {Expected} "
                    + "with state): the shortcut is off until the next start. Please report this "
                    + "with the Jellyfin version.",
                items.Count,
                result.Count,
                expected.Count
            );
            return expected;
        }

        if (
            expected.Keys.Any(streamTitles.Contains)
            || Interlocked.Increment(ref _resumeChecks) >= MaxResumeChecks
        )
        {
            Interlocked.CompareExchange(ref _resumeShortcut, Checked, Unchecked);
            _log.LogDebug("Resume state shortcut agrees with Jellyfin");
        }

        return expected;
    }

    private IReadOnlyDictionary<Guid, VersionResumeData> ResumeWithShortcut(
        IReadOnlyList<BaseItem> items,
        User user,
        out HashSet<Guid> streamTitles
    )
    {
        streamTitles = [];
        var candidates = items
            .OfType<Video>()
            .Where(v => !v.PrimaryVersionId.HasValue && v.LinkedAlternateVersions.Length > 0)
            .DistinctBy(v => v.Id)
            .ToList();
        if (candidates.Count == 0)
        {
            return _inner.GetResumeUserDataBatch(items, user);
        }

        var owners = _idLookup.VersionOwners(candidates.Select(v => v.Id).ToList());
        var mine = candidates
            .Where(v =>
                owners.TryGetValue(v.Id, out var owner)
                && owner is { HasOtherVersions: false, StreamRows.Count: > 0 }
            )
            .ToList();
        if (mine.Count == 0)
        {
            return _inner.GetResumeUserDataBatch(items, user);
        }

        var mineIds = streamTitles = mine.Select(v => v.Id).ToHashSet();
        var result = new Dictionary<Guid, VersionResumeData>(
            _inner.GetResumeUserDataBatch(items.Where(i => !mineIds.Contains(i.Id)).ToList(), user)
        );

        var withState = _idLookup.WithUserData(
            user.Id,
            mine.SelectMany(v => owners[v.Id].StreamRows).ToList()
        );
        var groups = new List<(Guid PrimaryId, List<BaseItem> Versions)>();
        foreach (var video in mine)
        {
            // The order Jellyfin compares them in: the item, then its versions by sort name.
            var versions = new List<BaseItem> { video };
            versions.AddRange(
                owners[video.Id]
                    .StreamRows.Where(withState.Contains)
                    .Select(id => _libraryManager.Value.GetItemById(id))
                    .OfType<Video>()
                    .OrderBy(v => v.SortName)
            );
            groups.Add((video.Id, versions));
        }

        var data = _inner.GetUserDataBatch(
            groups.SelectMany(g => g.Versions).DistinctBy(i => i.Id).ToList(),
            user
        );
        foreach (var (primaryId, versions) in groups)
        {
            var resume = VersionPlaybackSelector.SelectMostRecentlyPlayed(
                versions,
                version => data.GetValueOrDefault(version.Id),
                d => d.PlaybackPositionTicks > 0 || d.Played
            );
            if (resume is not null)
            {
                result[primaryId] = new VersionResumeData(resume.Id, data[resume.Id]);
            }
        }

        return result;
    }

    public UserItemDataDto? GetUserDataDto(
        BaseItem item,
        BaseItemDto? itemDto,
        User user,
        DtoOptions options
    ) => _inner.GetUserDataDto(item, itemDto, user, options);

    public bool UpdatePlayState(BaseItem item, UserItemData data, long? reportedPositionTicks) =>
        _inner.UpdatePlayState(item, data, reportedPositionTicks);

    public void ResetPlaybackStreamSelections(User user, BaseItem item) =>
        _inner.ResetPlaybackStreamSelections(user, item);
}
