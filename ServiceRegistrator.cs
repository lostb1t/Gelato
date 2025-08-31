using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExternalMedia;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<ExternalMediaStremioProvider>();

        services.AddSingleton<ExternalMediaSearchActionFilter>();
        services.AddSingleton<ExternalMediaInsertActionFilter>();
        services.AddSingleton<ExternalMediaManager>();
        services.AddSingleton<ExternalMediaSeriesManager>();
        services.AddSingleton<ExternalMediaRefresh>();
        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
        {

            o.Filters.AddService<ExternalMediaSearchActionFilter>();
            o.Filters.AddService<ExternalMediaInsertActionFilter>();
        });


    }
}
