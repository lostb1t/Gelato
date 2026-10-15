using System.Diagnostics;
using Gelato.Config;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Gelato.Controllers;

/// <summary>
/// Asks an addon URL for its manifest before it is saved, so the settings page and its setup can
/// say whether it answers and what it offers.
/// </summary>
[ApiController]
[Route("gelato/addon")]
[Authorize(Policy = Policies.RequiresElevation)]
public class AddonController(IHttpClientFactory http, ILoggerFactory log) : ControllerBase
{
    /// <summary>The URL comes in the body: it carries the user's addon config.</summary>
    public sealed class CheckRequest
    {
        public string Url { get; set; } = "";
    }

    [HttpPost("check")]
    public async Task<ActionResult<object>> Check([FromBody] CheckRequest request)
    {
        string baseUrl;
        try
        {
            baseUrl = new PluginConfiguration { Url = request.Url }.GetBaseUrl();
        }
        catch (InvalidOperationException)
        {
            return BadRequest("Enter the AIOStreams URL.");
        }

        if (
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            return BadRequest("This is no http or https URL.");
        }

        // A provider of its own: the factory's are cached per URL, and this one may never be saved.
        var provider = new GelatoStremioProvider(
            baseUrl,
            http,
            log.CreateLogger<GelatoStremioProvider>()
        );
        var watch = Stopwatch.StartNew();
        var manifest = await provider.GetManifestAsync(force: true).ConfigureAwait(false);
        watch.Stop();
        if (manifest is null)
        {
            return Ok(
                new
                {
                    Ok = false,
                    Error = "No addon manifest answered at this URL.",
                    Milliseconds = watch.ElapsedMilliseconds,
                }
            );
        }

        var catalogs = manifest
            .Catalogs.Select(c => new
            {
                c.Id,
                c.Type,
                c.Name,
                Importable = c.IsImportable(),
                Search = c.IsSearchCapable(),
            })
            .ToList();
        bool Has(string name) =>
            manifest.Resources.Any(r =>
                string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)
            );

        return Ok(
            new
            {
                Ok = true,
                manifest.Name,
                manifest.Version,
                manifest.Description,
                manifest.Logo,
                Milliseconds = watch.ElapsedMilliseconds,
                Streams = Has("stream"),
                Meta = Has("meta"),
                Subtitles = Has("subtitles"),
                SearchMovie = catalogs.Any(c => c.Search && c.Type == "movie"),
                SearchSeries = catalogs.Any(c => c.Search && c.Type == "series"),
                Catalogs = catalogs,
            }
        );
    }
}
