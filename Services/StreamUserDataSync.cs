using Gelato.Decorators;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Keeps the watch state of a movie/episode in step with its stream rows.
/// </summary>
/// <remarks>
/// Jellyfin 12 keeps a resume point on the version that was played, because local versions can be
/// different cuts. A movie's streams are the same film from different sources, and the list
/// changes between syncs, so playback of a stream moves the movie's resume point. The movie holds
/// the shared state, and DtoServiceDecorator shows it on every version. Played state needs no copy:
/// Jellyfin marks every version itself. Favourites and ratings set on a stream are applied to the
/// movie by <see cref="Filters.StreamUserDataFilter"/>: the saved event carries the stream's whole
/// stored state, which is stale apart from the field that was set.
/// Listens to <see cref="UserDataManagerDecorator.ItemSaved"/>: the rows' saves are not forwarded
/// to other listeners, so the copy on the movie is what they see, with the stream's reason.
/// </remarks>
public sealed class StreamUserDataSync(
    UserDataManagerDecorator userDataManager,
    IUserManager userManager,
    ILibraryManager libraryManager,
    ILogger<StreamUserDataSync> log
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        userDataManager.ItemSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        userDataManager.ItemSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <summary>
    /// How much later than the stream the movie's copy of the last played date is set. The movie
    /// wins Jellyfin's most-recently-played choice among the versions, and a stream is the more
    /// recent one only when it was played after the movie by at least this much.
    /// </summary>
    public static readonly TimeSpan CopyOffset = TimeSpan.FromTicks(1);

    /// <summary>
    /// The resume point a stream just gave its movie, for the played-state pass that follows on
    /// the same thread.
    /// </summary>
    [ThreadStatic]
    private static (Guid UserId, Guid PrimaryId, long Position, DateTime At)? _stopped;

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason is UserDataSaveReason.TogglePlayed)
        {
            RestoreResumePoint(e);
            return;
        }

        if (
            e.Item is not Video row
            || !row.HasStreamTag()
            || e.SaveReason
                is not (
                    UserDataSaveReason.PlaybackStart
                    or UserDataSaveReason.PlaybackProgress
                    or UserDataSaveReason.PlaybackFinished
                )
        )
        {
            return;
        }

        // A session that started on the row before it was adopted as a version still holds the
        // old instance; the owner is on the current one. Rows being deleted are unlinked first
        // (on the current instance too), so clearing their watch state is not copied.
        if (
            (
                row.PrimaryVersionId
                ?? (libraryManager.GetItemById(row.Id) as Video)?.PrimaryVersionId
            )
            is not { } primaryId
        )
        {
            return;
        }

        try
        {
            if (
                userManager.GetUserById(e.UserId) is not { } user
                || libraryManager.GetItemById(primaryId) is not Video primary
                || userDataManager.GetUserData(user, primary) is not { } data
            )
            {
                return;
            }

            var source = e.UserData;
            // One tick after the stream: Jellyfin's resume query keeps one in-progress version per
            // title and breaks a tie on the item id, so with equal dates Continue Watching showed
            // the movie for some titles and the stream row for others.
            data.LastPlayedDate = source.LastPlayedDate + CopyOffset ?? data.LastPlayedDate;

            // A start report sets no position: the stream's stored one is stale, and a stream that
            // fails before its first progress report would clear the movie's resume point.
            if (e.SaveReason is not UserDataSaveReason.PlaybackStart)
            {
                data.PlaybackPositionTicks = source.PlaybackPositionTicks;
            }

            // With the stream's reason: the stream's own save is not forwarded to other listeners,
            // so this copy is the playback they see, on the movie.
            userDataManager.SaveUserData(user, primary, data, e.SaveReason, CancellationToken.None);

            // Replaying a watched movie: the stream is watched too, so Jellyfin marks every other
            // version watched again right after this and resets their resume points, the movie's
            // included. It does so on every progress report as well as on stop, so the stream's
            // own point goes back onto the movie after each of them.
            _stopped =
                e.SaveReason is not UserDataSaveReason.PlaybackStart
                && source.Played
                && source.PlaybackPositionTicks > 0
                    ? (e.UserId, primaryId, source.PlaybackPositionTicks, DateTime.UtcNow)
                    : null;
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Could not copy watch state of stream {Id} to {PrimaryId}",
                row.Id,
                primaryId
            );
        }
    }

    private void RestoreResumePoint(UserDataSaveEventArgs e)
    {
        if (
            _stopped is not { } stopped
            || e.Item.Id != stopped.PrimaryId
            || e.UserId != stopped.UserId
            || e.UserData is not { Played: true, PlaybackPositionTicks: 0 } data
        )
        {
            return;
        }

        _stopped = null;
        if (DateTime.UtcNow - stopped.At > TimeSpan.FromSeconds(5))
            return;

        try
        {
            if (userManager.GetUserById(e.UserId) is not { } user)
                return;

            data.PlaybackPositionTicks = stopped.Position;
            // A repair of what the stream's copy already announced, not a change for listeners.
            using (userDataManager.Quiet())
            {
                userDataManager.SaveUserData(
                    user,
                    e.Item,
                    data,
                    UserDataSaveReason.UpdateUserData,
                    CancellationToken.None
                );
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not keep the resume point of {Id}", e.Item.Id);
        }
    }
}
