using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;
namespace Jellyfin.Plugin.ExternalMedia;

public class ExternalMovie : Movie
{
  // public override string GetClientTypeName() => nameof(Movie);
    public override IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution)
    {
        var sources = base.GetMediaSources(enablePathSubstitution).ToList();
Console.WriteLine($"[ExternalMovie] {Name} returning {sources.Count} sources");
        // Example: keep Stremio/native order or any rule you want.
        // Replace this with your actual preference:
        //   - by provider tag
        //   - by path pattern
        //   - by bitrate, codec, etc.
        return sources
            .OrderBy(s => s.Path, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Optional: make user data keys distinct so watchstate doesn’t collide with normal Episode
    
}

public class ExternalSeries : Series
{
   public override string GetClientTypeName() => nameof(Series);
    public override IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution)
    {
        var sources = base.GetMediaSources(enablePathSubstitution).ToList();
Console.WriteLine($"[ExternalMovie] {Name} returning {sources.Count} sources");
        // Example: keep Stremio/native order or any rule you want.
        // Replace this with your actual preference:
        //   - by provider tag
        //   - by path pattern
        //   - by bitrate, codec, etc.
        return sources
            .OrderBy(s => s.Path, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Optional: make user data keys distinct so watchstate doesn’t collide with normal Episode
    
}