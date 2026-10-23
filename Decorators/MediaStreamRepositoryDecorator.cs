using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace Gelato.Decorators;

/// <summary>
/// Thrown when Gelato's probe would save the streams of an item that was deleted while it ran.
/// </summary>
public sealed class ProbedItemDeletedException(Guid id)
    : Exception($"{id} was deleted while it was probed");

/// <summary>
/// Ends Gelato's probe of an item that was deleted while it ran, before Jellyfin saves its streams.
/// </summary>
/// <remarks>
/// Jellyfin's probe saves the streams, attachments and chapters itself. A pre-probe of an episode
/// whose series was deleted meanwhile failed there on the foreign key of the missing item, and
/// the database logged the failed save as an error with its stack, three times over. Only during
/// Gelato's probe (<see cref="SubtitleManagerDecorator.Probing"/>): a scan saves the streams of
/// files it just saved itself.
/// </remarks>
public sealed class MediaStreamRepositoryDecorator(
    IMediaStreamRepository inner,
    Lazy<ILibraryManager> libraryManager
) : IMediaStreamRepository
{
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery filter) =>
        inner.GetMediaStreams(filter);

    public IReadOnlyList<string> GetMediaStreamLanguages(MediaStreamType mediaStreamType) =>
        inner.GetMediaStreamLanguages(mediaStreamType);

    public void SaveMediaStreams(
        Guid id,
        IReadOnlyList<MediaStream> streams,
        CancellationToken cancellationToken
    )
    {
        // Asked of the database: an episode deleted with its series stays in the library's cache.
        if (SubtitleManagerDecorator.Probing.Value && libraryManager.Value.RetrieveItem(id) is null)
            throw new ProbedItemDeletedException(id);

        inner.SaveMediaStreams(id, streams, cancellationToken);
    }
}
