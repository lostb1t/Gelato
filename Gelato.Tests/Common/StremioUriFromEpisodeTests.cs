using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using NSubstitute;

namespace Gelato.Tests.Common;

[Collection("BaseItem statics")]
public sealed class StremioUriFromEpisodeTests : IDisposable
{
    private readonly ILibraryManager _previous = BaseItem.LibraryManager;
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();

    public StremioUriFromEpisodeTests()
    {
        BaseItem.LibraryManager = _library;
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = _previous;
    }

    [Fact]
    public void Episode_WithSeriesImdbAndNumbers_BuildsSeasonEpisodeId()
    {
        var series = SeriesWithImdb("tt0903747");
        var episode = new Episode
        {
            SeriesId = series.Id,
            ParentIndexNumber = 1,
            IndexNumber = 2,
        };

        Assert.Equal(
            "stremio://series/tt0903747:1:2",
            StremioUri.FromBaseItem(episode)?.ToString()
        );
    }

    [Fact]
    public void Episode_MissingEpisodeNumber_FallsBackToStremioProviderId()
    {
        var series = SeriesWithImdb("tt0903747");
        var episode = new Episode { SeriesId = series.Id, ParentIndexNumber = 1 };
        episode.SetProviderId("Stremio", "tt0903747:1:2");

        Assert.Equal(
            "stremio://series/tt0903747:1:2",
            StremioUri.FromBaseItem(episode)?.ToString()
        );
    }

    [Fact]
    public void Episode_MissingSeasonNumber_AndNoStremioId_ReturnsNull()
    {
        var series = SeriesWithImdb("tt0903747");
        var episode = new Episode { SeriesId = series.Id, IndexNumber = 2 };

        Assert.Null(StremioUri.FromBaseItem(episode));
    }

    [Fact]
    public void Episode_WithoutSeries_ReturnsNull()
    {
        // SeriesId and ParentId are both empty, so Episode.Series is null without any lookup.
        var episode = new Episode { ParentIndexNumber = 1, IndexNumber = 2 };

        Assert.Null(StremioUri.FromBaseItem(episode));
    }

    private Series SeriesWithImdb(string imdb)
    {
        var series = new Series { Id = Guid.NewGuid() };
        series.SetProviderId(MetadataProvider.Imdb, imdb);
        _library.GetItemById(series.Id).Returns(series);
        return series;
    }
}
