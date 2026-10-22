using System.Globalization;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators.MediaSources;

/// <summary>
/// Builds a movie's or an episode's version list: its stream rows as media sources, this user's
/// only, in the addon's order, with the first one under the item's id.
/// </summary>
internal sealed class VersionList(
    MediaSourceManagerDecorator decorator,
    IMediaSourceManager inner,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IHttpContextAccessor http,
    Lazy<GelatoManager> manager
)
{
    private readonly IMediaSourceManager _inner = inner;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<MediaSourceManagerDecorator> _log = log;
    private readonly IHttpContextAccessor _http = http;
    private readonly Lazy<GelatoManager> _manager = manager;

    internal IReadOnlyList<MediaSourceInfo> BuildStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User? user
    )
    {
        var manager = _manager.Value;
        _log.LogDebug("GetStaticMediaSources {Id}", item.Id);
        var userId =
            user?.Id
            ?? _http.ReadRequest(ctx => ctx.TryGetUserId(out var id) ? id : Guid.Empty, Guid.Empty);

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            (!cfg.EnableMixed && !item.IsGelatoPlaybackItem())
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
        )
        {
            var own = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

            // A local movie keeps the stream rows linked while mixed mode was on. Jellyfin would
            // list them with their stream URLs and without the per-user filter.
            if (item is Video { LinkedAlternateVersions.Length: > 0 } localVideo)
            {
                var linkedStreams = GetStreamRowIds(GetStreamRows(localVideo));
                if (linkedStreams.Count > 0)
                {
                    return own.Where(s => !linkedStreams.Contains(s.Id)).ToList();
                }
            }

            return own;
        }

        // A stream row is one version of its movie/episode. Jellyfin 12's web client loads it as
        // an item when the version dropdown changes and rebuilds the dropdown from its sources.
        var isStreamRow = item.IsStreamRow();

        var uri = StremioUri.FromBaseItem(item);
        var actionName = _http.ReadRequest(ctx => ctx.GetActionName(), null);

        var allowSync =
            _http.ReadRequest(ctx => ctx.IsInsertableAction(), false) && userId != Guid.Empty;
        var video = item as Video;
        var syncItemId = video?.PrimaryVersionId ?? item.Id;
        // With the creation date: an item deleted and inserted again gets the same id (its path
        // is hashed), but its rows went with it, so it must sync anew within StreamTTL.
        var cacheKey = StreamSyncOnOpen.SyncCacheKey(item, userId);

        if (!allowSync)
        {
            _log.LogDebug(
                "GetStaticMediaSources not a sync-eligible call. action={Action} uri={Uri}",
                actionName,
                uri?.ToString()
            );
        }
        else if (uri is not null && !isStreamRow && !manager.HasStreamSync(cacheKey, syncItemId))
        {
            decorator.Sync.SyncNow(item, userId, uri, cacheKey);
        }

        var itemId = item.Id.ToString("N", CultureInfo.InvariantCulture);

        // A search result the library does not hold: no row has its id, so it has no versions,
        // no rows to link, no media streams and no attachments, and the four queries asking for
        // them, per result of a search, can only come back empty. Not when a sync was allowed:
        // that one may just have written rows for it.
        var notInLibrary = !allowSync && item.IsNotInLibrary();

        // A version, a stream row or a file merged in by hand, lists the versions of its movie.
        var primary = video?.PrimaryVersionId is { } primaryVersionId
            ? _libraryManager.GetItemById(primaryVersionId) as Video
            : video;
        var linkedVersions =
            primary is null || notInLibrary
                ? []
                : _libraryManager.GetLinkedAlternateVersions(primary).ToList();
        if (
            linkedVersions.Count == 0
            && !notInLibrary
            && primary is not null
            && !isStreamRow
            && primary.IsGelatoPlaybackItem()
            && manager.RelinkOwnedRows(primary)
        )
        {
            linkedVersions = _libraryManager.GetLinkedAlternateVersions(primary).ToList();
        }
        var streamRows = linkedVersions
            .Where(v => v.IsStreamRow())
            .OrderBy(v => VersionOrder.IsShortClip(v, primary))
            .ThenBy(v => v.GelatoData<int?>("index") ?? int.MaxValue)
            .ToList();
        var streamRowIds = GetStreamRowIds(streamRows);

        // A subtitle downloaded or uploaded on the movie/episode's own page is saved for the
        // movie/episode, which no version plays, so it is offered with every one of its streams.
        // It was picked by hand for the title, and a file saved under one row would go with the
        // row when the addon's answer changes.
        var titleSubtitles =
            streamRows.Count > 0 && primary is not null && primary.IsGelatoPlaybackItem()
                ? primary.GetGelatoSubtitleFiles().ToList()
                : null;

        // Jellyfin lists the linked stream rows itself, named after the item; they are added
        // below with their stream names, and only the ones this user has. A stream row's own
        // Jellyfin source is the row itself. A Gelato movie/episode has no media of its own, so
        // unless versions were merged in by hand, Jellyfin's list is skipped: building it costs
        // several queries per stream.
        // A stream row of a local movie lists the movie's own file too.
        var mediaOwner = isStreamRow ? primary : item;
        var hasOwnMedia =
            mediaOwner is not null
            && (!mediaOwner.IsGelatoPlaybackItem() || linkedVersions.Any(v => !v.IsStreamRow()));
        var sources = !hasOwnMedia
            ? []
            : _inner
                .GetStaticMediaSources(mediaOwner!, enablePathSubstitution, user)
                .Where(s => !streamRowIds.Contains(s.Id))
                .ToList();

        var versions = streamRows
            .Where(x =>
                userId == Guid.Empty
                || (x.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
            )
            .Select(row =>
            {
                var source = GetVersionInfo(row, MediaSourceType.Grouping, user, titleSubtitles);

                if (user is not null)
                {
                    _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
                }

                return (Row: row, Source: source);
            })
            .ToList();

        // Before the first stream takes the episode's id below: the group's stream is the one
        // that plays under it.
        if (primary is not null)
            decorator.Order.MoveBingeGroupFirst(versions, primary, userId);

        _log.LogDebug(
            "Found {Count} streams. UserId={UserId} ItemId={ItemId}",
            versions.Count,
            userId,
            item.Id
        );

        sources.AddRange(versions.Select(v => v.Source));

        if (isStreamRow)
        {
            // The requested version goes first: it becomes the Default source.
            var own =
                sources.FirstOrDefault(s => s.Id == itemId)
                ?? GetVersionInfo(item, MediaSourceType.Grouping, user, titleSubtitles);
            sources.Remove(own);
            sources.Insert(0, own);
        }

        if (sources.Count > 1)
        {
            // remove primary from list when there are streams
            sources = sources
                .Where(k => !(k.Path?.IsPlaceholderPath() ?? false))
                .ToList();
        }

        // failsafe. mediasources cannot be null
        if (sources.Count == 0)
        {
            sources.Add(
                GetVersionInfo(item, MediaSourceType.Default, user, inLibrary: !notInLibrary)
            );
        }

        // A Gelato movie/episode has no media of its own, so its first stream takes its id and the
        // movie is one of its versions: clients that play the source with the item's id get the
        // first stream, and its watch state stays on the movie. Version pages list it with the
        // same id, the first stream's own page included, so picking it there opens the movie and
        // playing it there reports progress on the movie.
        var primaryId = primary?.Id.ToString("N", CultureInfo.InvariantCulture);
        if (
            primaryId is not null
            && sources.All(s => s.Id != primaryId)
            && versions.FirstOrDefault().Source is { } first
        )
        {
            first.Id = primaryId;
        }

        if (!isStreamRow && primary is not null && user is not null)
        {
            decorator.Order.MoveResumedVersionFirst(sources, primary, versions, user);
        }

        foreach (var source in sources)
        {
            if (source.Type == MediaSourceType.Default)
                source.Type = MediaSourceType.Grouping;
        }
        sources[0].Type = MediaSourceType.Default;

        // A page opened or another version picked: the web client loads the version's row as an
        // item when the dropdown changes, so this is also the pick of a version.
        if (
            user is not null
            && _http.ReadRequest(
                ctx => ctx.GetActionName() is "GetItem" or "GetItemLegacy" && RequestsMediaSources(ctx),
                false
            )
        )
        {
            decorator.PreProbes.SchedulePreProbe(item, sources[0], user);
        }

        return sources;
    }

    /// <summary>
    /// Whether an item request wants the item's sources, as a page does: Jellyfin Web's details
    /// page sends no Fields and gets every field. A script that asks for some fields of the item
    /// (KefinTweaks' home sections ask for People) has not opened its page.
    /// </summary>
    private static bool RequestsMediaSources(HttpContext ctx) =>
        !ctx.Request.Query.TryGetValue("fields", out var fields)
        || fields.Any(value =>
            value
                ?.Split(',', StringSplitOptions.TrimEntries)
                .Contains("MediaSources", StringComparer.OrdinalIgnoreCase) ?? false
        );

    /// <summary>
    /// The stream rows linked to a movie/episode as its versions. Read from the database: the
    /// instance at hand may be a copy whose links are out of date.
    /// </summary>
    internal List<Video> GetStreamRows(Video? primary) =>
        primary is null
            ? []
            : _libraryManager
                .GetLinkedAlternateVersions(primary)
                .Where(v => v.IsStreamRow())
                .ToList();

    internal static HashSet<string> GetStreamRowIds(IEnumerable<Video> rows) =>
        rows.Select(r => r.Id.ToString("N", CultureInfo.InvariantCulture)).ToHashSet();

    private MediaSourceInfo GetVersionInfo(
        BaseItem item,
        MediaSourceType type,
        User? user = null,
        IReadOnlyList<(string Path, string Language, string Codec)>? titleSubtitles = null,
        bool inLibrary = true
    )
    {
        ArgumentNullException.ThrowIfNull(item);

        var streamName = item.GelatoData<string>("name");
        var streamDesc = item.GelatoData<string>("description");
        var bingeGroup = item.GelatoData<string>("bingeGroup");
        var richName = !string.IsNullOrEmpty(streamDesc)
            ? $"{streamName}\n{streamDesc}"
            : streamName;

        var info = new MediaSourceInfo
        {
            Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
            ETag = item.Id.ToString("N", CultureInfo.InvariantCulture),
            Protocol = MediaProtocol.Http,
            MediaStreams = GetMediaStreamsWithExternalSubs(item, titleSubtitles, inLibrary),
            MediaAttachments = inLibrary ? _inner.GetMediaAttachments(item.Id) : [],
            Name = richName,
            Path = item.Path,
            RunTimeTicks = item.RunTimeTicks,
            Container = item.Container,
            Size = item.Size,
            Type = type,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            // just always say yes
            HasSegments = true,
            //HasSegments = MediaSegmentManager.HasSegments(item.Id)
        };


        if (user is not null)
        {
            info.SupportsTranscoding = user.HasPermission(
                PermissionKind.EnableVideoPlaybackTranscoding
            );
            info.SupportsDirectStream = user.HasPermission(PermissionKind.EnablePlaybackRemuxing);
        }
        if (string.IsNullOrEmpty(info.Path))
        {
            info.Type = MediaSourceType.Placeholder;
        }

        if (item is Video video)
        {
            info.IsoType = video.IsoType;
            info.VideoType = video.VideoType;
            info.Video3DFormat = video.Video3DFormat;
            info.Timestamp = video.Timestamp;
            info.IsRemote = true;

            if (video.IsShortcut)
            {
                info.IsRemote = true;
                info.Path = video.ShortcutPath;
            }
        }

        info.Bitrate = item.TotalBitrate;
        info.InferTotalBitrate();

        return info;
    }

    // Jellyfin's MediaInfoResolver.GetExternalStreamsAsync bails immediately when !video.IsFileProtocol
    // (stream items have http:// paths). This means external subtitle files saved to the internal
    // metadata folder are never discovered during library refresh and never written to the DB.
    // We work around this by scanning the metadata folder ourselves at playback time and merging
    // any matching subtitle files into the DB streams on the fly. A stream row lists the files of
    // its movie/episode after its own.
    private IReadOnlyList<MediaStream> GetMediaStreamsWithExternalSubs(
        BaseItem item,
        IReadOnlyList<(string Path, string Language, string Codec)>? titleSubtitles = null,
        bool inLibrary = true
    )
    {
        var streams = inLibrary ? _inner.GetMediaStreams(item.Id).ToList() : [];

        var existingPaths = new HashSet<string>(
            streams.Where(s => s.Path != null).Select(s => s.Path!),
            StringComparer.OrdinalIgnoreCase
        );

        var nextIndex = streams.Count > 0 ? streams.Max(s => s.Index) + 1 : 0;

        // A probe records the subtitle files next to the row as streams, and a probed row is not
        // probed again: a file deleted since stayed listed and its delivery answered 404. Left out
        // after nextIndex is taken, so the files listed below keep their indexes.
        streams.RemoveAll(s =>
            s.IsExternal
            && s.IsExternalUrl != true
            && !string.IsNullOrEmpty(s.Path)
            && !File.Exists(s.Path)
        );

        // Oldest first, the row's and the title's together: a subtitle saved after a PlaybackInfo
        // (the download after a probe) takes the next index, and the indexes a player already has
        // keep their file. In the folder's order, or the row's files before the title's, a new file
        // moved the others and the player's request got another subtitle or none.
        var files = item.GetGelatoSubtitleFiles()
            .Concat(titleSubtitles ?? [])
            .OrderBy(f => File.GetLastWriteTimeUtc(f.Path))
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Where(f => !existingPaths.Contains(f.Path))
            .ToList();
        if (files.Count == 0)
            return streams;

        // A file keeps the index it was listed with, and a deleted file's index is not given to
        // the next one: numbered by position, deleting a file moved every newer one down, and a
        // player that had the list got another subtitle for the index it asked for.
        var given = _subtitleIndexes.GetOrAdd(
            item.Id,
            _ => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        );
        lock (given)
        {
            var next = given.Count > 0 ? Math.Max(nextIndex, given.Values.Max() + 1) : nextIndex;
            foreach (var (file, langCode, codec) in files)
            {
                if (!given.TryGetValue(file, out var index) || index < nextIndex)
                {
                    index = next++;
                    given[file] = index;
                }

                streams.Add(
                    new MediaStream
                    {
                        Type = MediaStreamType.Subtitle,
                        IsExternal = true,
                        IsExternalUrl = false,
                        SupportsExternalStream = true,
                        Path = file,
                        Language = langCode,
                        Codec = codec,
                        Index = index,
                        IsDefault = false,
                        IsForced = false,
                        IsHearingImpaired = false,
                    }
                );
            }
        }

        return streams;
    }

    // The indexes GetMediaStreamsWithExternalSubs gave the subtitle files it lists from disk, by
    // row, the files deleted since included.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        Guid,
        Dictionary<string, int>
    > _subtitleIndexes = new();
}
