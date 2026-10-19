using Gelato.Decorators;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;

namespace Gelato.Services;

/// <summary>
/// The titles put into the library because someone opened them from search, and that nobody has
/// played, marked a favourite or added to a collection or playlist since
/// (<see cref="Config.PluginConfiguration.HideUnplayedSearchTitles"/>). Listings leave them out
/// (<see cref="GelatoItemRepository.GetPendingIds"/>), and
/// <see cref="ScheduledTasks.RemoveUnplayedSearchTitlesTask"/> deletes them after a while.
/// </summary>
/// <remarks>
/// Marked with <see cref="Tag"/> on the item, like <see cref="GelatoManager.TreeSyncedTag"/>, so
/// the mark lives in the database with the item. A metadata refresh keeps it: Gelato's refreshes
/// merge tags, and only a "replace all metadata" refresh drops it, which lists the title again
/// and keeps it from being removed.
/// </remarks>
public sealed class PendingSearchTitles(
    IItemPersistenceService persistence,
    Lazy<ILibraryManager> libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    ItemIdLookup lookup
)
{
    public const string Tag = "gelato-search-pending";

    /// <summary>The ids of the marked titles, read by index.</summary>
    public Guid[] Ids() => lookup.WithTag(Tag);

    public static bool IsPending(BaseItem item) =>
        item.Tags?.Contains(Tag, StringComparer.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// Marks a title just opened from search, unless someone has played it or marked it a
    /// favourite: Jellyfin gives a title it deleted before its watch state back, without a save
    /// that would take the mark off again.
    /// </summary>
    public void Add(BaseItem item)
    {
        if (IsPending(item) || HasBeenUsed(item))
            return;

        item.Tags = [.. item.Tags ?? [], Tag];
        Save(item);
    }

    /// <summary>
    /// Takes the mark off the title <paramref name="item"/> belongs to: the item itself, a stream
    /// row's movie or episode, an episode's or season's series.
    /// </summary>
    public void Release(BaseItem? item)
    {
        if (item is null)
            return;

        Release(item.Id);
        if (item is Video { PrimaryVersionId: { } primary })
            Release(primary);
        if (item is Episode { SeriesId: var episodeSeries })
            Release(episodeSeries);
        if (item is Season { SeriesId: var seasonSeries })
            Release(seasonSeries);
    }

    public void Release(Guid id)
    {
        if (
            id == Guid.Empty
            || libraryManager.Value.GetItemById(id) is not { } item
            || !IsPending(item)
        )
            return;

        item.Tags = [.. item.Tags.Where(t => !t.Equals(Tag, StringComparison.OrdinalIgnoreCase))];
        Save(item);
    }

    /// <summary>Whether any user has played, started, favourited or liked <paramref name="item"/>.</summary>
    public bool HasBeenUsed(BaseItem item) =>
        userManager
            .GetUsers()
            .Any(user =>
                userDataManager.GetUserData(user, item) is { } data
                && (
                    data.Played
                    || data.IsFavorite
                    || data.Likes == true
                    || data.PlaybackPositionTicks > 0
                    || data.PlayCount > 0
                )
            );

    // As the tree sync's mark: saved straight, and the library manager's copy is this one.
    private void Save(BaseItem item)
    {
        persistence.SaveItems([item], CancellationToken.None);
        libraryManager.Value.RegisterItem(item);
    }
}

/// <summary>
/// Takes a title off <see cref="PendingSearchTitles"/> when someone plays it or marks it a
/// favourite or likes it (the "watchlist" of KefinTweaks and other clients). A series counts as
/// played once any of its episodes is.
/// </summary>
public sealed class PendingSearchTitlesRelease(
    UserDataManagerDecorator userDataManager,
    PendingSearchTitles pending
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        userDataManager.ItemSaved += OnSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        userDataManager.ItemSaved -= OnSaved;
        return Task.CompletedTask;
    }

    private void OnSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (
            e.SaveReason
                is UserDataSaveReason.PlaybackStart
                    or UserDataSaveReason.PlaybackProgress
                    or UserDataSaveReason.PlaybackFinished
            || e.UserData is { Played: true } or { IsFavorite: true } or { Likes: true }
        )
            pending.Release(e.Item);
    }
}
