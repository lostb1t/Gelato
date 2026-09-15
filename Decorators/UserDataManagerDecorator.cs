using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

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

    public UserDataManagerDecorator(IUserDataManager inner)
    {
        _inner = inner;
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
    ) => _inner.SaveUserData(user, item, userData, reason, cancellationToken);

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

    public IReadOnlyDictionary<Guid, VersionResumeData> GetResumeUserDataBatch(
        IReadOnlyList<BaseItem> items,
        User user
    ) => _inner.GetResumeUserDataBatch(items, user);

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
