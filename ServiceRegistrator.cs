using System.Reflection;
using Gelato.Decorators;
using Gelato.Filters;
using Gelato.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Dto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gelato;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<GelatoStremioProvider>();
        services.AddSingleton<InsertActionFilter>();
        services.AddSingleton<SearchActionFilter>();
        //services.AddSingleton<RedirectActionFilter>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.AddSingleton<ImageResourceFilter>();
        services.AddSingleton<DeleteResourceFilter>();
        services.AddSingleton<DownloadFilter>();
        services.AddSingleton<GelatoManager>();
        services.AddSingleton(sp =>
            new Lazy<GelatoManager>(() => sp.GetRequiredService<GelatoManager>()));

        //services.AddSingleton<IScheduledTask, GelatoCatalogItemsSyncTask>();
        //services.AddSingleton<IScheduledTask, GelatoCatalogItemsSyncTask>();
        //services.AddSingleton<IScheduledTask, SyncRunningSeriesTask>();
         // services.AddSingleton<IScheduledTask, PurgeGelatoStreamsTask>();
      
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


        var originalDto = services.First(sd => sd.ServiceType == typeof(IDtoService));
        services.Remove(originalDto);

        services.AddSingleton<IDtoService>(sp =>
        {
            IDtoService inner =
               originalDto.ImplementationInstance as IDtoService
               ?? (IDtoService)(originalDto.ImplementationFactory?.Invoke(sp)
                   ?? ActivatorUtilities.CreateInstance(sp, originalDto.ImplementationType!));

            return ActivatorUtilities.CreateInstance<DtoServiceDecorator>(sp, inner);
        });

        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
        {
           // o.Filters.AddService<RedirectActionFilter>(order: 0);
            o.Filters.AddService<InsertActionFilter>(order: 1);
            o.Filters.AddService<SearchActionFilter>(order: 2);
            o.Filters.AddService<PlaybackInfoFilter>(order: 3);
            //o.Filters.AddService<SourceActionFilter>(order: 3);
            o.Filters.AddService<ImageResourceFilter>();
            o.Filters.AddService<DeleteResourceFilter>();
            o.Filters.AddService<DownloadFilter>();
        });
    }
}
