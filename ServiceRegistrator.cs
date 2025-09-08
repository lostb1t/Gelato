using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;
using System.Reflection;
using Gelato.Filters;
using MediaBrowser.Model.Tasks;
using Gelato.Tasks;


namespace Gelato;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<GelatoStremioProvider>();

        services.AddSingleton<InsertActionFilter>();
        services.AddSingleton<SearchActionFilter>();
        services.AddSingleton<SourceActionFilter>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.AddSingleton<ImageResourceFilter>();
        // services.AddSingleton<DeleteResourceFilter>();

        services.AddSingleton<GelatoManager>();
        services.AddSingleton<IMediaSourceProvider, GelatoSourceProvider>();
        services.AddSingleton<IScheduledTask, GelatoCatalogItemsSyncTask>();

        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
        {
            o.Filters.AddService<InsertActionFilter>(order: 0);
            o.Filters.AddService<SearchActionFilter>(order: 1);
            o.Filters.AddService<PlaybackInfoFilter>(order: 2);
            o.Filters.AddService<SourceActionFilter>(order: 3);
            o.Filters.AddService<ImageResourceFilter>();
            // o.Filters.AddService<DeleteResourceFilter>();
        });
    }
}
