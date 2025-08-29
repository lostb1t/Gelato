using MediaBrowser.Model.Plugins;
using System;

namespace Jellyfin.Plugin.ExternalMedia.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public Guid? MovieLibraryId { get; set; } = Guid.Parse("f137a2dd21bbc1b99aa5c0f6bf02a805");

        public string Url { get; set; } = "https://aiostreams.sjoerdarendsen.dev/stremio/6d684d6b-629d-4a14-b629-2fe01db3a1e0/eyJpdiI6IjgwZGN3UXlaVWk2YWlaZTNXVEFFS0E9PSIsImVuY3J5cHRlZCI6IjltK2J5RnFxN3kreElGU3liRU1FSFE9PSIsInR5cGUiOiJhaW9FbmNyeXB0In0/manifest.json";

        public string GetBaseUrl()
        {
            if (string.IsNullOrWhiteSpace(Url))
                throw new InvalidOperationException("ExternalMedia Url not configured.");

            var u = Url.Trim().TrimEnd('/');

            if (u.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                u = u[..^"/manifest.json".Length];

            return u;
        }

        public Guid? GetMovieLibrary()
        {
            if (MovieLibraryId is null)
                return null;


        }
    }
}