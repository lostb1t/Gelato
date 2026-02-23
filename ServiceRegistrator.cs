using Gelato.Config;
using Gelato.Decorators;
using Gelato.Filters;
using Gelato.Providers;
using Gelato.Services;
//using IntroDbPlugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.Configuration;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato;

public class ServiceRegistrator : IPluginServiceRegistrator {
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host) {
        services.AddSingleton<InsertActionFilter>();
        services.AddSingleton<SearchActionFilter>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.AddSingleton<ImageResourceFilter>();
        services.AddSingleton<DeleteResourceFilter>();
        services.AddSingleton<DownloadFilter>();
        services.AddSingleton<GelatoManager>();
        services.DecorateSingle<IItemRepository, GelatoItemRepository>();
        services.AddSingleton(sp => (GelatoItemRepository)sp.GetRequiredService<IItemRepository>());
        services.AddSingleton<GelatoStremioProviderFactory>();
        services.AddSingleton(sp => new Lazy<GelatoManager>(sp.GetRequiredService<GelatoManager>));
        services.AddSingleton<CatalogService>();
        services.AddSingleton<CatalogImportService>();
        services.AddSingleton<PalcoCacheService>();
        services.AddSingleton<ISubtitleProvider, SubtitleProvider>();
       
        // Register HttpClient for IntroDbClient
        services.AddHttpClient<IntroDbClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.introdb.app");
            client.Timeout = TimeSpan.FromSeconds(IntroDbClient.DefaultTimeoutSeconds);
        });
        services.AddSingleton<IMediaSegmentProvider, IntroDbSegmentProvider>();

        services.AddHostedService<GelatoService>();
        services
            .DecorateSingle<IDtoService, DtoServiceDecorator>()
            .DecorateSingle<IMediaSourceManager, MediaSourceManagerDecorator>()
            .DecorateSingle<ICollectionManager, CollectionManagerDecorator>()
            .DecorateSingle<IPlaylistManager, PlaylistManagerDecorator>()
            .DecorateSingle<ISubtitleManager, SubtitleManagerDecorator>();
        services.AddSingleton(sp => new Lazy<ISubtitleManager>(sp.GetRequiredService<ISubtitleManager>));


        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => {
            o.Filters.AddService<InsertActionFilter>(order: 1);
            o.Filters.AddService<SearchActionFilter>(order: 2);
            o.Filters.AddService<PlaybackInfoFilter>(order: 3);
            o.Filters.AddService<ImageResourceFilter>();
            o.Filters.AddService<DeleteResourceFilter>();
            o.Filters.AddService<DownloadFilter>();
        });
    }

    public class GelatoService(IConfiguration config, ILogger<GelatoService> log)
        : IHostedService {
        public Task StartAsync(CancellationToken cancellationToken) {
            var analyze = GelatoPlugin.Instance?.Configuration?.FFmpegAnalyzeDuration ?? "5M";
            var probe = GelatoPlugin.Instance?.Configuration?.FFmpegProbeSize ?? "40M";

            config["FFmpeg:probesize"] = probe;
            config["FFmpeg:analyzeduration"] = analyze;

            log.LogInformation(
                "Gelato: set FFmpeg:probesize={Probe}, FFmpeg:analyzeduration={Analyze}",
                probe,
                analyze
            );
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public static class ServiceCollectionDecorationExtensions {
    private static object BuildInner(IServiceProvider sp, ServiceDescriptor d) {
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
}
