using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;
using System.Reflection;

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
        
        //var mediaSourceManagerProxy = DispatchProxy.Create<IMediaSourceManager, MediaSourceManagerProxy>();
       // services.AddSingleton<IMediaSourceManager>(mediaSourceManagerProxy);

        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
        {
            o.Filters.AddService<ExternalMediaInsertActionFilter>(order: 0);
            o.Filters.AddService<ExternalMediaSourceActionFilter>(order: 1);
            o.Filters.AddService<ExternalMediaSearchActionFilter>(order: 2);
        });


    }
}
