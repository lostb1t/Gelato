using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Gelato.Decorators;

/// <summary>
/// Keeps Gelato's stream rows out of similar items and movie recommendations.
/// </summary>
/// <remarks>
/// Jellyfin 12's "Local Genre/Tag" provider scores candidates straight on the database, so the
/// stream-row filter in <see cref="GelatoItemRepository"/> never applies. Stream rows share the
/// <c>gelato-stream</c> tag, and some carry their movie's genres, studios and people, so they
/// outscore everything else: a movie's page listed its own versions as similar items. Jellyfin
/// drops alternate versions by PrimaryVersionId, which rows synced before they were linked as
/// versions do not have.
/// </remarks>
public sealed class SimilarItemsManagerDecorator(
    ISimilarItemsManager inner,
    ILibraryManager libraryManager
) : ISimilarItemsManager
{
    // Jellyfin's default when the client sends no limit.
    private const int DefaultLimit = 50;

    // Every dropped stream row leaves a gap, so similar items are asked for again with twice the
    // limit, at most this many times.
    private const int MaxAttempts = 4;

    // Recommendation categories are picked at random per call, so they cannot be asked for again.
    // Ask once for more items per category instead.
    private const int RecommendationItemFactor = 4;

    public void AddParts(IEnumerable<ISimilarItemsProvider> providers) => inner.AddParts(providers);

    public IReadOnlyList<ISimilarItemsProvider> GetSimilarItemsProviders<T>()
        where T : BaseItem => inner.GetSimilarItemsProviders<T>();

    public async Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(
        BaseItem item,
        IReadOnlyList<Guid> excludeArtistIds,
        User? user,
        DtoOptions dtoOptions,
        int? limit,
        LibraryOptions? libraryOptions,
        CancellationToken cancellationToken
    )
    {
        // A stream row is a version of its movie: the tag every stream row shares would make the
        // other movies' rows its most similar items.
        if (
            item.HasStreamTag()
            && (item as Video)?.PrimaryVersionId is { } primaryId
            && libraryManager.GetItemById(primaryId) is { } movie
        )
        {
            item = movie;
        }

        var wanted = limit ?? DefaultLimit;
        var request = wanted;

        for (var attempt = 1; ; attempt++)
        {
            var items = await inner
                .GetSimilarItemsAsync(
                    item,
                    excludeArtistIds,
                    user,
                    dtoOptions,
                    request,
                    libraryOptions,
                    cancellationToken
                )
                .ConfigureAwait(false);

            var kept = items.Where(i => !i.HasStreamTag()).ToList();
            if (kept.Count >= wanted || items.Count < request || attempt == MaxAttempts)
            {
                return kept.Take(wanted).ToList();
            }

            request = (int)Math.Min((long)request * 2, int.MaxValue);
        }
    }

    public async Task<IReadOnlyList<SimilarItemsRecommendation>> GetMovieRecommendationsAsync(
        User? user,
        Guid parentId,
        int categoryLimit,
        int itemLimit,
        DtoOptions dtoOptions,
        CancellationToken cancellationToken
    )
    {
        var request = (int)Math.Min((long)itemLimit * RecommendationItemFactor, int.MaxValue);
        var categories = await inner
            .GetMovieRecommendationsAsync(
                user,
                parentId,
                categoryLimit,
                request,
                dtoOptions,
                cancellationToken
            )
            .ConfigureAwait(false);

        var result = new List<SimilarItemsRecommendation>(categories.Count);
        foreach (var category in categories)
        {
            var items = category.Items.Where(i => !i.HasStreamTag()).Take(itemLimit).ToList();
            if (items.Count == 0)
                continue;

            result.Add(
                new SimilarItemsRecommendation
                {
                    BaselineItemName = category.BaselineItemName,
                    CategoryId = category.CategoryId,
                    RecommendationType = category.RecommendationType,
                    Items = items,
                }
            );
        }

        return result;
    }
}
