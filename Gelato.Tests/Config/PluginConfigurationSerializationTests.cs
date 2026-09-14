using System.Xml.Serialization;
using Gelato.Config;

namespace Gelato.Tests.Config;

public class PluginConfigurationSerializationTests
{
    [Fact]
    public void Defaults_Serialize_WithoutThrowing()
    {
        var xml = Serialize(new PluginConfiguration());

        Assert.Contains("<PluginConfiguration", xml);
        Assert.DoesNotContain("Stremio", xml); // [XmlIgnore] fields must stay out
    }

    [Fact]
    public void NonDefaultValues_RoundTrip_Unchanged()
    {
        var userId = Guid.NewGuid();
        var original = new PluginConfiguration
        {
            Url = "https://aio.example.com/abc/manifest.json",
            MoviePath = "/data/gelato/movies",
            SeriesPath = "/data/gelato/series",
            StreamTTL = 120,
            CatalogMaxItems = 7,
            EnableMixed = true,
            FilterUnreleased = true,
            FilterUnreleasedBufferDays = 3,
            DisableSourceCount = false,
            P2PEnabled = true,
            P2PDLSpeed = 1024,
            CreateCollections = true,
            LastSeenServerVersion = "12.0.0",
            Catalogs =
            [
                new CatalogConfig
                {
                    Id = "top",
                    Type = "series",
                    Name = "Top",
                    Enabled = true,
                    MaxItems = 5,
                    CreateCollection = true,
                },
            ],
            UserConfigs =
            [
                new UserConfig
                {
                    UserId = userId,
                    Url = "https://aio.example.com/kid/manifest.json",
                    MoviePath = "/data/kid/movies",
                    SeriesPath = "/data/kid/series",
                    DisableSearch = true,
                },
            ],
        };

        var restored = Deserialize(Serialize(original));

        Assert.Equal(original.Url, restored.Url);
        Assert.Equal(original.MoviePath, restored.MoviePath);
        Assert.Equal(original.SeriesPath, restored.SeriesPath);
        Assert.Equal(original.StreamTTL, restored.StreamTTL);
        Assert.Equal(original.CatalogMaxItems, restored.CatalogMaxItems);
        Assert.Equal(original.EnableMixed, restored.EnableMixed);
        Assert.Equal(original.FilterUnreleased, restored.FilterUnreleased);
        Assert.Equal(original.FilterUnreleasedBufferDays, restored.FilterUnreleasedBufferDays);
        Assert.Equal(original.DisableSourceCount, restored.DisableSourceCount);
        Assert.Equal(original.P2PEnabled, restored.P2PEnabled);
        Assert.Equal(original.P2PDLSpeed, restored.P2PDLSpeed);
        Assert.Equal(original.CreateCollections, restored.CreateCollections);
        Assert.Equal(original.LastSeenServerVersion, restored.LastSeenServerVersion);

        var catalog = Assert.Single(restored.Catalogs);
        Assert.Equal("top", catalog.Id);
        Assert.Equal("series", catalog.Type);
        Assert.True(catalog.Enabled);
        Assert.Equal(5, catalog.MaxItems);
        Assert.True(catalog.CreateCollection);

        var user = Assert.Single(restored.UserConfigs);
        Assert.Equal(userId, user.UserId);
        Assert.Equal("/data/kid/movies", user.MoviePath);
        Assert.True(user.DisableSearch);
    }

    [Fact]
    public void GetBaseUrl_StripsManifestSuffixAndTrailingSlash()
    {
        var cfg = new PluginConfiguration { Url = "https://aio.example.com/abc/manifest.json/" };

        Assert.Equal("https://aio.example.com/abc", cfg.GetBaseUrl());
    }

    [Fact]
    public void GetBaseUrl_Throws_WhenUnconfigured()
    {
        var cfg = new PluginConfiguration { Url = "   " };

        Assert.Throws<InvalidOperationException>(() => cfg.GetBaseUrl());
    }

    private static string Serialize(PluginConfiguration cfg)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, cfg);
        return writer.ToString();
    }

    private static PluginConfiguration Deserialize(string xml)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }
}
