using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Gelato.Tests.Common;

public class StremioUriTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankExternalId(string? externalId)
    {
        Assert.Throws<ArgumentException>(() => new StremioUri(StremioMediaType.Movie, externalId));
    }

    [Fact]
    public void ToString_Movie_WithoutStream()
    {
        var uri = new StremioUri(StremioMediaType.Movie, "tt0111161");

        Assert.Equal("stremio://movie/tt0111161", uri.ToString());
    }

    [Fact]
    public void ToString_Series_WithStream()
    {
        var uri = new StremioUri(StremioMediaType.Series, "tt0903747:1:2", "abc");

        Assert.Equal("stremio://series/tt0903747:1:2/abc", uri.ToString());
    }

    [Fact]
    public void ToString_BlankStreamId_IsTreatedAsNone()
    {
        var uri = new StremioUri(StremioMediaType.Movie, "tt0111161", "   ");

        Assert.Equal("stremio://movie/tt0111161", uri.ToString());
    }

    /// <summary>
    /// ToGuid() is the Jellyfin item ID for every Gelato item in a user's library. These
    /// expectations were computed from the production implementation. If this test fails,
    /// existing libraries will orphan on upgrade — change the derivation only deliberately,
    /// with a migration, and update these values in the same commit.
    /// </summary>
    [Theory]
    [InlineData(StremioMediaType.Movie, "tt0111161", null, "a9285927-29f0-9e66-b169-d50acfa84566")]
    [InlineData(StremioMediaType.Series, "tt0903747", null, "4c9c1c88-1f4c-a1f7-e20b-da196a397d28")]
    [InlineData(
        StremioMediaType.Series,
        "tt0903747:1:2",
        null,
        "650580bf-e5d3-4d21-47c9-354edef0f67d"
    )]
    [InlineData(
        StremioMediaType.Movie,
        "tt0111161",
        "somestream",
        "c4165978-24c6-fadf-e431-2668988a9c5f"
    )]
    public void ToGuid_IsStableForKnownInputs(
        StremioMediaType type,
        string externalId,
        string? streamId,
        string expected
    )
    {
        var uri = new StremioUri(type, externalId, streamId);

        Assert.Equal(Guid.Parse(expected), uri.ToGuid());
    }

    [Fact]
    public void FromBaseItem_Movie_PrefersImdb()
    {
        var movie = new Movie();
        movie.SetProviderId("Stremio", "kitsu:1");
        movie.SetProviderId(MetadataProvider.Imdb, "tt0111161");

        Assert.Equal("stremio://movie/tt0111161", StremioUri.FromBaseItem(movie)?.ToString());
    }

    [Fact]
    public void FromBaseItem_Movie_FallsBackToStremioProviderId()
    {
        var movie = new Movie();
        movie.SetProviderId("Stremio", "kitsu:1");

        Assert.Equal("stremio://movie/kitsu:1", StremioUri.FromBaseItem(movie)?.ToString());
    }

    [Fact]
    public void FromBaseItem_Movie_WithNoIds_ReturnsNull()
    {
        Assert.Null(StremioUri.FromBaseItem(new Movie()));
    }

    [Fact]
    public void FromBaseItem_Series_UsesImdb()
    {
        var series = new Series();
        series.SetProviderId(MetadataProvider.Imdb, "tt0903747");

        Assert.Equal("stremio://series/tt0903747", StremioUri.FromBaseItem(series)?.ToString());
    }

    [Fact]
    public void FromBaseItem_UnsupportedKind_Throws()
    {
        Assert.Throws<NotSupportedException>(() => StremioUri.FromBaseItem(new Audio()));
    }

    [Fact]
    public void FromBaseItem_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => StremioUri.FromBaseItem(null!));
    }
}
