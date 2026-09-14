using Jellyfin.Data.Enums;

namespace Gelato.Tests.Common;

public class EnumMappingTests
{
    [Theory]
    [InlineData(BaseItemKind.Movie, StremioMediaType.Movie)]
    [InlineData(BaseItemKind.Series, StremioMediaType.Series)]
    [InlineData(BaseItemKind.Season, StremioMediaType.Series)]
    [InlineData(BaseItemKind.Episode, StremioMediaType.Series)]
    [InlineData(BaseItemKind.Audio, StremioMediaType.Unknown)]
    [InlineData(BaseItemKind.Folder, StremioMediaType.Unknown)]
    public void ToStremio_MapsKinds(BaseItemKind kind, StremioMediaType expected)
    {
        Assert.Equal(expected, kind.ToStremio());
    }

    [Theory]
    [InlineData(StremioMediaType.Movie, BaseItemKind.Movie)]
    [InlineData(StremioMediaType.Series, BaseItemKind.Series)]
    public void ToBaseItem_MapsSupportedTypes(StremioMediaType type, BaseItemKind expected)
    {
        Assert.Equal(expected, type.ToBaseItem());
    }

    [Theory]
    [InlineData(StremioMediaType.Unknown)]
    [InlineData(StremioMediaType.Episode)]
    [InlineData(StremioMediaType.Channel)]
    public void ToBaseItem_RejectsUnsupportedTypes(StremioMediaType type)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => type.ToBaseItem());
    }
}
