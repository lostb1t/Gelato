using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

public sealed class GelatoSeasonMetadataProvider(ILogger<GelatoSeasonMetadataProvider> log)
    : IRemoteMetadataProvider<Season, SeasonInfo>,
        IHasOrder
{
    public string Name => "Gelato";
    public int Order => 0;

    public async Task<MetadataResult<Season>> GetMetadata(
        SeasonInfo info,
        CancellationToken cancellationToken
    )
    {
        var result = new MetadataResult<Season> { HasMetadata = false, QueriedById = true };

        info.SeriesProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var seriesImdbId);
        if (string.IsNullOrWhiteSpace(seriesImdbId))
            info.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out seriesImdbId);

        var seasonNumber = info.IndexNumber;

        if (string.IsNullOrWhiteSpace(seriesImdbId))
        {
            log.LogDebug("GelatoSeasonMetadataProvider: no series IMDB id for {Name}", info.Name);
            return result;
        }

        var stremio = GelatoPlugin.Instance?.Configuration.Stremio;
        if (stremio is null)
            return result;

        StremioMeta? seriesMeta;
        try
        {
            seriesMeta =
                stremio.GetCachedMeta(seriesImdbId)
                ?? await stremio
                    .GetMetaAsync(seriesImdbId, StremioMediaType.Series)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "GelatoSeasonMetadataProvider: failed to fetch series meta for {Id}",
                seriesImdbId
            );
            return result;
        }

        if (seriesMeta is null || !seriesMeta.IsValid())
            return result;

        stremio.CacheMeta(seriesImdbId, seriesMeta);

        result.HasMetadata = true;
        result.Item = MapSeason(seriesMeta, info.Name, seasonNumber);
        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        SeasonInfo searchInfo,
        CancellationToken cancellationToken
    ) => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    private static Season MapSeason(StremioMeta seriesMeta, string? seasonName, int? seasonNumber)
    {
        var season = new Season
        {
            Name = string.IsNullOrWhiteSpace(seasonName)
                ? (seasonNumber.HasValue ? $"Season {seasonNumber}" : "Season")
                : seasonName,
            IndexNumber = seasonNumber,
            PremiereDate = seriesMeta.GetPremiereDate(),
            ProductionYear = seriesMeta.GetYear(),
        };

        // Use season-specific poster when available
        if (
            seasonNumber.HasValue
            && seriesMeta.App_Extras?.SeasonPosters is { } posters
            && seasonNumber.Value > 0
            && seasonNumber.Value <= posters.Count
        )
        {
            var poster = posters[seasonNumber.Value - 1];
            if (!string.IsNullOrWhiteSpace(poster))
                season.SetProviderId("StremioSeasonPoster", poster);
        }

        return season;
    }
}
