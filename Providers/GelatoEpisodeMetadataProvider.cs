using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

public sealed class GelatoEpisodeMetadataProvider(ILogger<GelatoEpisodeMetadataProvider> log)
    : IRemoteMetadataProvider<Episode, EpisodeInfo>,
        IHasOrder
{
    public string Name => "Gelato";
    public int Order => 0;

    public async Task<MetadataResult<Episode>> GetMetadata(
        EpisodeInfo info,
        CancellationToken cancellationToken
    )
    {
        var result = new MetadataResult<Episode> { HasMetadata = false, QueriedById = true };

        // Episode meta requires a series IMDB id + season + episode numbers
        info.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var seriesImdbId);
        if (string.IsNullOrWhiteSpace(seriesImdbId))
            info.SeriesProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out seriesImdbId);

        var season = info.ParentIndexNumber;
        var episode = info.IndexNumber;

        if (string.IsNullOrWhiteSpace(seriesImdbId) || season is null || episode is null)
        {
            log.LogDebug(
                "GelatoEpisodeMetadataProvider: missing series IMDB id or season/episode numbers for {Name}",
                info.Name
            );
            return result;
        }

        var stremio = GelatoPlugin.Instance?.Configuration.Stremio;
        if (stremio is null)
            return result;

        // The episode-level Stremio ID for fetching streams is "imdbId:season:episode",
        // but we fetch it from the parent series meta (same endpoint as series).
        var seriesId = seriesImdbId;
        StremioMeta? seriesMeta;
        try
        {
            seriesMeta =
                stremio.GetCachedSeriesMeta(seriesId)
                ?? await stremio
                    .GetMetaAsync(seriesId, StremioMediaType.Series)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "GelatoEpisodeMetadataProvider: failed to fetch series meta for {Id}",
                seriesId
            );
            return result;
        }

        if (seriesMeta is null || !seriesMeta.IsValid())
            return result;

        stremio.CacheSeriesMeta(seriesId, seriesMeta);

        // Find the matching episode in Videos[]
        var epMeta = seriesMeta.Videos?.FirstOrDefault(v =>
            v.Season == season && (v.Episode ?? v.Number) == episode
        );

        if (epMeta is null)
        {
            log.LogDebug(
                "GelatoEpisodeMetadataProvider: no episode meta found for S{Season}E{Episode} in {SeriesId}",
                season,
                episode,
                seriesId
            );
            return result;
        }

        result.HasMetadata = true;
        result.Item = MapEpisode(epMeta, season.Value, episode.Value);
        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        EpisodeInfo searchInfo,
        CancellationToken cancellationToken
    ) => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    private static Episode MapEpisode(StremioMeta ep, int season, int episode)
    {
        var item = new Episode
        {
            Name = ep.GetName(),
            Overview = ep.Description ?? ep.Overview,
            IndexNumber = episode,
            ParentIndexNumber = season,
            PremiereDate = ep.FirstAired ?? ep.Released ?? ep.GetPremiereDate(),
        };

        if (!string.IsNullOrWhiteSpace(ep.Thumbnail))
            item.SetProviderId("StremioThumb", ep.Thumbnail);

        var tvdbId = ep.TvdbEpisodeId();
        if (tvdbId is not null)
            item.SetProviderId(MetadataProvider.Tvdb, tvdbId);

        return item;
    }
}
