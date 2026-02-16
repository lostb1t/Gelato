using System.Reflection;
using Gelato.Configuration;
using Gelato.Decorators;
using Gelato.Filters;
using Gelato.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato;

public class ServiceRegistrator : IPluginServiceRegistrator {
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host) {
        //services.AddSingleton<GelatoStremioProvider>();
        services.AddSingleton<InsertActionFilter>();
        services.AddSingleton<SearchActionFilter>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.AddSingleton<ImageResourceFilter>();
      //  services.AddSingleton<DeleteResourceFilter>();
        services.AddSingleton<DownloadFilter>();
        services.AddSingleton<GelatoManager>();
        // services.DecorateSingle<ISubtitleManager, GelatoSubtitleManager>();
        services.DecorateSingle<IItemRepository, GelatoItemRepository>();
        services.AddSingleton(sp => (GelatoItemRepository)sp.GetRequiredService<IItemRepository>());
        services.AddSingleton<GelatoStremioProviderFactory>();
        services.AddSingleton(sp => new Lazy<GelatoManager>(() =>
            sp.GetRequiredService<GelatoManager>()
        ));
        services.AddHostedService<GelatoService>();

        services
            .DecorateSingle<IDtoService, DtoServiceDecorator>()
            .DecorateSingle<IMediaSourceManager, MediaSourceManagerDecorator>();
        // .DecorateSingle<IFileSystem, FileSystemDecorator>();


        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => {
            o.Filters.AddService<InsertActionFilter>(order: 1);
            o.Filters.AddService<SearchActionFilter>(order: 2);
            o.Filters.AddService<PlaybackInfoFilter>(order: 3);
            o.Filters.AddService<ImageResourceFilter>();
          //  o.Filters.AddService<DeleteResourceFilter>();
            o.Filters.AddService<DownloadFilter>();
        });
    }

    public class GelatoService : IHostedService {
        private readonly IConfiguration _config;
        private readonly ILogger<GelatoService> _log;

        public GelatoService(IConfiguration config, ILogger<GelatoService> log) {
            _config = config;
            _log = log;
        }

        public async Task StartAsync(CancellationToken cancellationToken) {
            var analyze = GelatoPlugin.Instance?.Configuration?.FFmpegAnalyzeDuration ?? "5M";
            var probe = GelatoPlugin.Instance?.Configuration?.FFmpegProbeSize ?? "40M";

            _config["FFmpeg:probesize"] = probe;
            _config["FFmpeg:analyzeduration"] = analyze;

            _log.LogInformation(
                "Gelato: set FFmpeg:probesize={Probe}, FFmpeg:analyzeduration={Analyze}",
                probe,
                analyze
            );
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public static class ServiceCollectionDecorationExtensions {
    static object BuildInner(IServiceProvider sp, ServiceDescriptor d) {
        if (d.ImplementationInstance is not null)
            return d.ImplementationInstance;
        if (d.ImplementationFactory is not null)
            return d.ImplementationFactory(sp);
        return ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
    }

    public static IServiceCollection DecorateSingle<TService, TDecorator>(
        this IServiceCollection services
    )
        where TDecorator : class, TService {
        var original = services.LastOrDefault(sd => sd.ServiceType == typeof(TService));
        if (original is null)
            return services; // nothing to decorate

        services.Remove(original);

        services.Add(
            new ServiceDescriptor(
                typeof(TService),
                sp => {
                    var inner = (TService)BuildInner(sp, original);
                    return ActivatorUtilities.CreateInstance<TDecorator>(sp, inner);
                },
                original.Lifetime
            )
        );

        return services;
    }

    public static IServiceCollection DecorateAll<TService, TDecorator>(
        this IServiceCollection services
    )
        where TDecorator : class, TService {
        var originals = services.Where(sd => sd.ServiceType == typeof(TService)).ToList();
        if (originals.Count == 0)
            return services;

        foreach (var d in originals)
            services.Remove(d);

        foreach (var d in originals) {
            services.Add(
                new ServiceDescriptor(
                    typeof(TService),
                    sp => {
                        var inner = (TService)BuildInner(sp, d);
                        return ActivatorUtilities.CreateInstance<TDecorator>(sp, inner);
                    },
                    d.Lifetime
                )
            );
        }

        return services;
    }
}
