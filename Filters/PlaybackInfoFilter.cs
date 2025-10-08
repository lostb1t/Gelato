using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Gelato.Filters
{
    /// <summary>
    /// Captures MediaSourceId for playback requests and patches PlaybackInfoResponse for Stremio items.
    /// </summary>
    public sealed class PlaybackInfoFilter : IAsyncActionFilter, IOrderedFilter
    {
        public int Order { get; set; } = 3;
        private const string Key = "MediaSourceId";

        public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
        {
            // Capture action name for logging/debug
            if (ctx.ActionDescriptor is ControllerActionDescriptor cad)
            {
                ctx.HttpContext.Items["actionName"] = cad.ActionName;
            }

            // Capture MediaSourceId once per request
            if (!ctx.HttpContext.Items.ContainsKey(Key))
            {
                if (TryFromArgs(ctx.ActionArguments, out var id) || TryFromQuery(ctx.HttpContext.Request, out id))
                {
                    if (!string.IsNullOrWhiteSpace(id))
                        ctx.HttpContext.Items[Key] = id!;
                }
            }

            var executed = await next();

            // Only continue if this is the PlaybackInfo endpoint
            if (!IsPlaybackInfo(ctx))
                return;

            if (executed.Result is not ObjectResult obj || obj.Value is not PlaybackInfoResponse resp)
                return;

            var mediaSourceId = ctx.HttpContext.Items.TryGetValue(Key, out var v) ? v as string : null;

            // while remote subtitles are cool and all. Most clients dont supports this. So just proxy the thing.
            foreach (var s in resp.MediaSources ?? Array.Empty<MediaSourceInfo>())
            {
              

                foreach (var stream in s.MediaStreams?.Where(ms => ms.IsTextSubtitleStream) ?? Enumerable.Empty<MediaStream>())
                {
                    if (stream.Type == MediaStreamType.Subtitle && (stream.IsExternalUrl ?? false))
                {
                    // Force Jellyfin to deliver subtitles locally
                    //stream.DeliveryMethod = MediaStreamDeliveryMethod.Hls;
                    //stream.IsExternal = false;
                    //stream.IsExternalUrl = false;
                    //stream.DeliveryUrl = $"/Videos/{s.Id}/{s.Id}/Subtitles/{stream.Index}/0/Stream.vtt";
                }
            }
                }
            
        }

        private static bool TryFromArgs(IDictionary<string, object?> args, out string? id)
        {
            foreach (var kv in args)
            {
                if (kv.Value is null) continue;
                if (kv.Key.Equals(Key, StringComparison.OrdinalIgnoreCase) && kv.Value is string s && !string.IsNullOrWhiteSpace(s))
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

        private static bool TryFromQuery(HttpRequest req, out string? id)
        {
            var val = req.Query[Key];
            if (!string.IsNullOrWhiteSpace(val))
            {
                id = val.ToString();
                return true;
            }

            id = null;
            return false;
        }

        private static bool IsPlaybackInfo(ActionExecutingContext ctx)
        {
            if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
                return false;

            return cad.ActionName.Equals("GetPlaybackInfo", StringComparison.OrdinalIgnoreCase)
                || cad.ActionName.Equals("PlaybackInfo", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStremioSource(MediaSourceInfo s)
        {
            return (s.Name?.Contains("stremio", StringComparison.OrdinalIgnoreCase) ?? false)
                   || (s.Path?.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase) ?? false);
        }
    }
}