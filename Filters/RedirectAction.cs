using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Jellyfin.Data.Enums;
using Gelato.Common;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;


namespace Gelato.Filters;

public class RedirectActionFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IDtoService _dtoService;
    private readonly GelatoStremioProvider _stremioProvider;
    private readonly ILogger<RedirectActionFilter> _log;
    private readonly GelatoManager _manager;
    private readonly IMediaSourceManager _sourceManager;
    private readonly IProviderManager _provider;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly LinkGenerator _links;
    private const string Key = "itemId";
    public int Order { get; set; } = 0;

    public RedirectActionFilter(
        ILibraryManager library,
        IFileSystem fileSystem,
        IItemRepository repo,
        IMediaSourceManager mediaSources,
        GelatoManager manager,
        IDtoService dtoService,
        GelatoStremioProvider stremioProvider,
        IProviderManager provider,
        ILibraryMonitor libraryMonitor,
        IMediaSourceManager sourceManager,
        ILogger<RedirectActionFilter> log,
        LinkGenerator links)
    {
        _library = library;
        _sourceManager = sourceManager;
        _repo = repo;
        _mediaSources = mediaSources;
        _dtoService = dtoService;
        _provider = provider;
        _stremioProvider = stremioProvider;
        _fileSystem = fileSystem;
        _manager = manager;
        _log = log;
        _libraryMonitor = libraryMonitor;
        _links = links;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
      
         var fullUrl = ctx.HttpContext.Request.GetDisplayUrl();
         _log.LogInformation("Requested URL: {Url}", fullUrl);
     
      if (TryFromArgs(ctx.ActionArguments, out var id))
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
              Guid guid = Guid.Parse(id);
              var meta = _manager.GetStremioMeta(guid);
        if (meta is not null)
        {
          
          var baseItem = _stremioProvider.IntoBaseItem(meta);
          
        var found = _manager.FindByProviderIds(baseItem.ProviderIds, baseItem.GetBaseItemKind());
        if (found is not null)
        {
            //_log.LogDebug($"InsertMeta: found existing item: {found.Id}");
           // _log.LogInformation("MEDIA already exists; redirecting to canonical id");
            _manager.ReplaceGuid(ctx, guid);
        }
        
        }

            }
        }

        await next();
    }
    
    private static bool TryFromArgs(IDictionary<string, object?> args, out string? id)
    {
        foreach (var kv in args)
        {
            if (kv.Value is null) continue;

            if (kv.Key.Equals(Key, System.StringComparison.OrdinalIgnoreCase)
                && kv.Value is string s && !string.IsNullOrWhiteSpace(s))
            {
                id = s;
                return true;
            }
        }

        foreach (var kv in args)
        {
            var v = kv.Value;
            if (v is null) continue;

            var prop = v.GetType().GetProperty(Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop?.GetValue(v) is string s && !string.IsNullOrWhiteSpace(s))
            {
                id = s;
                return true;
            }
        }

        id = null;
        return false;
    }
}