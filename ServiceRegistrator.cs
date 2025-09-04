using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ExternalMedia;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<ExternalMediaStremioProvider>();

        services.AddSingleton<ExternalMediaInsertActionFilter>();
        services.AddSingleton<ExternalMediaSearchActionFilter>();
        services.AddSingleton<ExternalMediaSourceActionFilter>();
        services.AddSingleton<ExternalMediaManager>();
        services.AddSingleton<ExternalMediaSeriesManager>();
        services.AddSingleton<IMediaSourceProvider, ExternalMediaSourceProvider>();
       // services.AddSingleton<ExternalMediaRefresh>();
        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
        {
            o.Filters.AddService<ExternalMediaInsertActionFilter>(order: 0);
            o.Filters.AddService<ExternalMediaSourceActionFilter>(order: 2);
            o.Filters.AddService<ExternalMediaSearchActionFilter>(order: 3);
        });


    }
}
