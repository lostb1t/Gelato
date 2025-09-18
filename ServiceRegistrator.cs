using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
//using MediaBrowser.Model.Dto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;
using System.Reflection;
using Gelato.Filters;
using MediaBrowser.Model.Tasks;
using Gelato.Tasks;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Persistence;
using Gelato.Decorators;

namespace Gelato;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<GelatoStremioProvider>();
        services.AddSingleton<InsertActionFilter>();
        services.AddSingleton<SearchActionFilter>();
        //services.AddSingleton<SourceActionFilter>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.AddSingleton<ImageResourceFilter>();
        // services.AddSingleton<DeleteResourceFilter>();
        services.AddSingleton<GelatoManager>();
        services.AddSingleton(sp =>
    new Lazy<GelatoManager>(() => sp.GetRequiredService<GelatoManager>()));
        //  services.AddSingleton<IMediaSourceProvider, GelatoSourceProvider>();
        services.AddSingleton<IScheduledTask, GelatoCatalogItemsSyncTask>();

        var original = services.First(sd => sd.ServiceType == typeof(IMediaSourceManager));
        services.Remove(original);

        services.AddSingleton<IMediaSourceManager>(sp =>
        {
            IMediaSourceManager inner =
                original.ImplementationInstance as IMediaSourceManager
                ?? (IMediaSourceManager)(original.ImplementationFactory?.Invoke(sp)
                    ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!));

            return ActivatorUtilities.CreateInstance<MediaSourceManagerDecorator>(sp, inner);
        });

        //var original_repo = services.First(sd => sd.ServiceType == typeof(IItemRepository));
       // services.Remove(original_repo);

       // services.AddSingleton<IItemRepository>(sp =>
        //{
       //     IItemRepository inner =
      //          original_repo.ImplementationInstance as IItemRepository
       //         ?? (IItemRepository)(original_repo.ImplementationFactory?.Invoke(sp)
       //             ?? ActivatorUtilities.CreateInstance(sp, original_repo.ImplementationType!));

        //    return ActivatorUtilities.CreateInstance<ItemRepositoryDecorator>(sp, inner);
        //});

        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
        {
            o.Filters.AddService<InsertActionFilter>(order: 0);
            o.Filters.AddService<SearchActionFilter>(order: 1);
            o.Filters.AddService<PlaybackInfoFilter>(order: 2);
            //o.Filters.AddService<SourceActionFilter>(order: 3);
            o.Filters.AddService<ImageResourceFilter>();
            // o.Filters.AddService<DeleteResourceFilter>();
        });
    }
}
