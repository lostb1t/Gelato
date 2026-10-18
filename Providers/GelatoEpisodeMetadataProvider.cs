using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

public sealed class GelatoEpisodeMetadataProvider(
    ILogger<GelatoEpisodeMetadataProvider> log,
    GelatoManager manager,
    IHttpClientFactory http
) : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    public string Name => "Gelato";
    public int Order => 0;

    public async Task<MetadataResult<Episode>> GetMetadata(
        EpisodeInfo info,
        CancellationToken cancellationToken
    )
    {
        var result = new MetadataResult<Episode> { HasMetadata = false, QueriedById = true };

        // Episode meta requires an id of the series the addon serves + season + episode numbers
        info.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var seriesImdbId);
        if (string.IsNullOrWhiteSpace(seriesImdbId))
            info.SeriesProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out seriesImdbId);

        var season = info.ParentIndexNumber;
        var episode = info.IndexNumber;

        if (season is null || episode is null)
        {
            log.LogDebug(
                "GelatoEpisodeMetadataProvider: missing season/episode numbers for {Name}",
                info.Name
            );
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
            // (lostb1t/Gelato#225). Only the series' ids, never the episode's - an episode carries
            // a TVDB id of its own, which names the episode rather than the show.
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
                "GelatoEpisodeMetadataProvider: failed to fetch series meta for {Id}",
                seriesId
            );
            return result;
        }

        if (seriesMeta is null || !seriesMeta.IsValid())
            return result;

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

        epMeta.Type = StremioMediaType.Episode;

        if (manager.IntoBaseItem(epMeta) is not Episode ep)
            return result;

        ep.ProviderIds.Remove("Stremio");
        ep.KeepEndDateOnlyForGelato(info.ProviderIds);
        result.HasMetadata = true;
        result.Item = ep;
        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        EpisodeInfo searchInfo,
        CancellationToken cancellationToken
    ) => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken
    ) => http.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);
}
