using Gelato.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

public sealed class GelatoMovieMetadataProvider(
    ILogger<GelatoMovieMetadataProvider> log,
    GelatoManager manager
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

        var id = ResolveId(info.ProviderIds);
        if (id is null)
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
            meta =
                stremio.GetCachedMeta(id)
                ?? await stremio.GetMetaAsync(id, StremioMediaType.Movie).ConfigureAwait(false);

            if (meta is not null)
                stremio.CacheMeta(id, meta);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "GelatoMovieMetadataProvider: failed to fetch meta for {Id}", id);
            return result;
        }

        if (meta is null || !meta.IsValid())
            return result;

        if (manager.IntoBaseItem(meta) is not Movie movie)
            return result;

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
    ) => throw new NotImplementedException();

    private static void MapPeople(StremioMeta meta, MetadataResult<Movie> result)
    {
        if (meta.App_Extras?.Cast is not { Count: > 0 } cast)
            return;

        foreach (var member in cast)
        {
            if (string.IsNullOrWhiteSpace(member.Name))
                continue;

            result.AddPerson(
                new PersonInfo
                {
                    Name = member.Name,
                    Role = member.Character,
                    Type = PersonKind.Actor,
                    ImageUrl = member.Photo,
                }
            );
        }
    }

    private static RemoteSearchResult ToSearchResult(StremioMeta meta) =>
        new()
        {
            Name = meta.GetName(),
            ProductionYear = meta.GetYear(),
            ImageUrl = meta.Poster ?? meta.Thumbnail,
            ProviderIds = meta.GetProviderIds(),
        };

    private static string? ResolveId(Dictionary<string, string> providerIds)
    {
        if (
            providerIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdb)
            && !string.IsNullOrWhiteSpace(imdb)
        )
            return imdb;
        if (
            providerIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdb)
            && !string.IsNullOrWhiteSpace(tmdb)
        )
            return $"tmdb:{tmdb}";
        return null;
    }

    private static GelatoStremioProvider? GetStremio() =>
        GelatoPlugin.Instance?.Configuration.Stremio;
}
