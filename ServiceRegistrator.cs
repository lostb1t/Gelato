using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExternalMedia;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {

        services.AddSingleton(new StremioOptions
        {
            BaseUrl = "https://aiostreams.sjoerdarendsen.dev/stremio/6d684d6b-629d-4a14-b629-2fe01db3a1e0/eyJpdiI6IjgwZGN3UXlaVWk2YWlaZTNXVEFFS0E9PSIsImVuY3J5cHRlZCI6IjltK2J5RnFxN3kreElGU3liRU1FSFE9PSIsInR5cGUiOiJhaW9FbmNyeXB0In0",
            Timeout = TimeSpan.FromSeconds(10)
        });
        services.AddScoped<ExternalMediaResultFilter>();

        services.AddSingleton<ExternalMediaStremioProvider>();

        services.Configure<MvcOptions>(o =>
        {
            o.Filters.AddService<ExternalMediaResultFilter>();
        });
    }
}
