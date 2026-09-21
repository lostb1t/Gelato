using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

public sealed class GelatoSeasonMetadataProvider(
    ILogger<GelatoSeasonMetadataProvider> log,
    IHttpClientFactory http
) : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
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

        if (
            string.IsNullOrWhiteSpace(seriesImdbId)
            && !GelatoStremioProvider.HasMetaId(info.SeriesProviderIds)
        )
        {
            log.LogDebug("GelatoSeasonMetadataProvider: no usable series id for {Name}", info.Name);
            return result;
        }

        var stremio = GelatoPlugin.Instance?.Configuration.Stremio;
        if (stremio is null)
            return result;

        // Only for the log lines below: the id the lookup starts from.
        var seriesId = string.IsNullOrWhiteSpace(seriesImdbId)
            ? info.SeriesProviderIds.GetValueOrDefault("Stremio", info.Name)
            : seriesImdbId;
        StremioMeta? seriesMeta;
        try
        {
            // Without an IMDb id the series' other ids are all there is to go by: the addon's own
            // one and the kitsu:/mal:/anilist:/anidb: namespaces an anime catalog hands out
            // (lostb1t/Gelato#225). Only the series' ids, never the season's - a season carries a
            // Stremio id of its own, the series id with the season number appended.
            seriesMeta = string.IsNullOrWhiteSpace(seriesImdbId)
                ? await stremio
                    .GetMetaAsync(info.SeriesProviderIds, StremioMediaType.Series)
                    .ConfigureAwait(false)
                : await stremio
                    .GetMetaAsync(seriesImdbId, StremioMediaType.Series)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "GelatoSeasonMetadataProvider: failed to fetch series meta for {Id}",
                seriesId
            );
            return result;
        }

        if (seriesMeta is null || !seriesMeta.IsValid())
            return result;

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
    ) => http.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);

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
