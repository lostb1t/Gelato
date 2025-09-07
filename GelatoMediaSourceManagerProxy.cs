

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.MediaInfo;                
using MediaBrowser.Controller.MediaEncoding;        
using MediaBrowser.Controller.Library;
//using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
//using Emby.Server.Implementations.Library;

namespace Gelato;

public class MediaSourceManagerProxy : DispatchProxy
{
    private IMediaSourceManager _inner = default!;

    public void Init(IMediaSourceManager inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    protected override object? Invoke(MethodInfo targetMethod, object?[]? args)
    {
        Console.WriteLine($"[Gelato] Intercept: IMediaSourceManager.{targetMethod}");

        if (targetMethod.Name == "GetMediaStreams")
        {
            Console.WriteLine($"[Gelato] Intercepted GetMediaStreams overload: {targetMethod}");

            var result = targetMethod.Invoke(_inner, args);
            var retType = targetMethod.ReturnType;

            if (typeof(Task).IsAssignableFrom(retType))
            {
                if (retType.IsGenericType)
                {
                    var tType = retType.GetGenericArguments()[0];
                    return InterceptTaskGeneric(result!, tType);
                }
                return (Task)result!;
            }

            return TransformResult(result);
        }

        return targetMethod.Invoke(_inner, args);
    }

    private object InterceptTaskGeneric(object taskObj, Type tType)
    {
        var method = typeof(MediaSourceManagerProxy)
            .GetMethod(nameof(InterceptTaskGenericCore), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(tType);

        return method.Invoke(this, new[] { taskObj })!;
    }

    private async Task<T> InterceptTaskGenericCore<T>(Task<T> task)
    {
        var value = await task.ConfigureAwait(false);
        var newValue = TransformResult(value);
        return (T)newValue!;
    }

    private object? TransformResult(object? value)
    {
        if (value is null) return null;

        try
        {
            if (value is System.Collections.Generic.IEnumerable<MediaStream> seq)
            {
                var filtered = seq
                    .Where(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio)
                    .Select(FixStream)
                    .ToArray();
                Console.WriteLine($"[Gelato] GetMediaStreams -> {filtered.Length} filtered streams");
                return filtered;
            }

            if (value is MediaStream[] arr)
            {
                var filtered = arr
                    .Where(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio)
                    .Select(FixStream)
                    .ToArray();
                Console.WriteLine($"[Gelato] GetMediaStreams -> {filtered.Length} filtered streams");
                return filtered;
            }

            if (value is MediaStream single)
            {
                return FixStream(single);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Gelato] TransformResult failed: {ex}");
        }

        return value;
    }

    private MediaStream FixStream(MediaStream s)
    {
        // Example tweaks — customize to your needs
        return s;
    }
}