using Gelato.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

public sealed class GelatoMovieMetadataProvider(
    ILogger<GelatoMovieMetadataProvider> log,
    GelatoManager manager,
    IHttpClientFactory http
) : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
{
    public string Name => "Gelato";
    public int Order => 0;

    public async Task<MetadataResult<Movie>> GetMetadata(
        MovieInfo info,
        CancellationToken cancellationToken
    )
    {
        var result = new MetadataResult<Movie> { HasMetadata = false, QueriedById = true };

        if (!GelatoStremioProvider.HasMetaId(info.ProviderIds))
        {
            log.LogDebug("GelatoMovieMetadataProvider: no usable ID for {Name}", info.Name);
            return result;
        }

        var stremio = GetStremio();
        if (stremio is null)
            return result;

        StremioMeta? meta;
        try
        {
            meta = await stremio
                .GetMetaAsync(info.ProviderIds, StremioMediaType.Movie)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "GelatoMovieMetadataProvider: failed to fetch meta for {Name}",
                info.Name
            );
            return result;
        }

        if (meta is null || !meta.IsValid())
            return result;

        await manager.EnrichMetaAsync(meta, cancellationToken).ConfigureAwait(false);

        if (manager.IntoBaseItem(meta) is not Movie movie)
            return result;

        movie.ProviderIds.Remove("Stremio");
        movie.KeepEndDateOnlyForGelato(info.ProviderIds);
        result.HasMetadata = true;
        result.Item = movie;
        MapPeople(meta, result);
        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        MovieInfo searchInfo,
        CancellationToken cancellationToken
    )
    {
        var stremio = GetStremio();
        if (stremio is null || string.IsNullOrWhiteSpace(searchInfo.Name))
            return [];

        try
        {
            var results = await stremio
                .SearchAsync(searchInfo.Name, StremioMediaType.Movie)
                .ConfigureAwait(false);
            return results.Select(ToSearchResult);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "GelatoMovieMetadataProvider: search failed for {Name}",
                searchInfo.Name
            );
            return [];
        }
    }

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken
    ) => http.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);

    private static void MapPeople(StremioMeta meta, MetadataResult<Movie> result)
    {
        foreach (var person in MetaPeople.From(meta))
            result.AddPerson(person);
    }

    private static RemoteSearchResult ToSearchResult(StremioMeta meta) =>
        new()
        {
            Name = meta.GetName(),
            ProductionYear = meta.GetYear(),
            ImageUrl = meta.Poster ?? meta.Thumbnail,
            ProviderIds = meta.GetProviderIds(),
        };

    private static GelatoStremioProvider? GetStremio() =>
        GelatoPlugin.Instance?.Configuration.Stremio;
}
