using System.Collections.Concurrent;
using Gelato.Providers;
using Gelato.RemuxDb;
using Gelato.Services;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Gelato.Decorators.MediaSources;

namespace Gelato.Decorators;

/// <summary>
/// Gelato's media sources. The work is done by the parts in <c>Decorators/MediaSources</c>: the
/// version list (<see cref="VersionList"/>, <see cref="VersionOrder"/>), the stream sync when a
/// page opens (<see cref="StreamSyncOnOpen"/>) and the preparation for playback
/// (<see cref="PlaybackPreparation"/>, <see cref="PreProbes"/>, <see cref="StreamProbe"/>,
/// <see cref="StreamFailover"/>, <see cref="SegmentRuns"/>). The rest of
/// <see cref="IMediaSourceManager"/> is passed through.
/// </summary>
public sealed class MediaSourceManagerDecorator : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner;
    private readonly IHttpContextAccessor _http;

    public MediaSourceManagerDecorator(
        IMediaSourceManager inner,
        ILibraryManager libraryManager,
        ILogger<MediaSourceManagerDecorator> log,
        IHttpContextAccessor http,
        IUserDataManager userDataManager,
        IDirectoryService directoryService,
        IServerConfigurationManager config,
        Lazy<ISubtitleManager> subtitleManager,
        Lazy<GelatoManager> manager,
        Lazy<SubtitleProvider> subtitleProvider,
        IMediaSegmentManager mediaSegmentManager,
        Lazy<IProviderManager> providerManager,
        RemuxDbService remuxDb,
        IHttpClientFactory httpClientFactory,
        SegmentLookups segmentLookups,
        SyncPlaySources syncPlaySources,
        Lazy<ISessionManager> sessionManager
    )
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(log);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(mediaSegmentManager);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(config);

        Sync = new StreamSyncOnOpen(libraryManager, log, manager, subtitleProvider);
        Versions = new VersionList(this, inner, libraryManager, log, http, manager);
        Order = new VersionOrder(this, libraryManager, userDataManager);
        Playback = new PlaybackPreparation(
            this,
            inner,
            libraryManager,
            log,
            http,
            manager,
            remuxDb,
            segmentLookups,
            syncPlaySources
        );
        Failover = new StreamFailover(this, log);
        PreProbes = new PreProbes(this, libraryManager, log, httpClientFactory, sessionManager);
        Probe = new StreamProbe(
            inner,
            libraryManager,
            log,
            directoryService,
            subtitleManager,
            manager,
            providerManager,
            remuxDb
        );
        Segments = new SegmentRuns(libraryManager, log, mediaSegmentManager, segmentLookups);
    }

    internal StreamSyncOnOpen Sync { get; }
    internal VersionList Versions { get; }
    internal VersionOrder Order { get; }
    internal PlaybackPreparation Playback { get; }
    internal StreamFailover Failover { get; }
    internal PreProbes PreProbes { get; }
    internal StreamProbe Probe { get; }
    internal SegmentRuns Segments { get; }

    /// <summary>
    /// The key under which a search request holds the sources it built, per item (see
    /// <see cref="BeginSearchSources"/>).
    /// </summary>
    private const string SearchSourcesKey = "GelatoSearchSources";

    /// <summary>
    /// Makes the sources built for an item during this request be built once. A search builds a
    /// title the library holds twice: Jellyfin's own search answers with the item, and Gelato's
    /// half answers with it again in the addon result's place, which drops Jellyfin's DTO. In
    /// the Infuse shape, which asks for media sources, that built each owned title's sources
    /// twice, with the queries for its versions each time. No sync runs in a search, so both
    /// builds give the same sources.
    /// </summary>
    public static void BeginSearchSources(HttpContext ctx) =>
        ctx.Items[SearchSourcesKey] =
            new ConcurrentDictionary<(Guid, bool, Guid?), IReadOnlyList<MediaSourceInfo>>();

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User? user = null
    )
    {
        var memo = _http.ReadRequest(
            ctx =>
                ctx.Items.TryGetValue(SearchSourcesKey, out var m)
                    ? m as ConcurrentDictionary<
                        (Guid, bool, Guid?),
                        IReadOnlyList<MediaSourceInfo>
                    >
                    : null,
            null
        );
        if (memo is null)
            return Versions.BuildStaticMediaSources(item, enablePathSubstitution, user);

        return memo.GetOrAdd(
            (item.Id, enablePathSubstitution, user?.Id),
            _ => Versions.BuildStaticMediaSources(item, enablePathSubstitution, user)
        );
    }

    public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken ct
    ) =>
        Playback.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct);

    /// <inheritdoc cref="VersionOrder.FollowBingeGroup"/>
    public void FollowBingeGroup(BaseItem played, string? mediaSourceId, BaseItem next, User user) =>
        Order.FollowBingeGroup(played, mediaSourceId, next, user);

    /// <inheritdoc cref="PreProbes.PreProbeDefaultAsync"/>
    public Task PreProbeDefaultAsync(BaseItem item, User user) =>
        PreProbes.PreProbeDefaultAsync(item, user);

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
    {
        _inner.AddParts(providers);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        return _inner.GetMediaStreams(itemId);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
    {
        return _inner.GetMediaStreams(query).ToList();
    }

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) =>
        _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) =>
        _inner.GetMediaAttachments(query);

    public Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string? liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken
    ) =>
        _inner.GetMediaSource(
            item,
            mediaSourceId,
            liveStreamId,
            enablePathSubstitution,
            cancellationToken
        );

    public async Task<LiveStreamResponse> OpenLiveStream(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStream(request, cancellationToken);

    public async Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStream(id, cancellationToken);

    public Task<
        Tuple<MediaSourceInfo, IDirectStreamProvider>
    > GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) =>
        _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
        ActiveRecordingInfo info,
        CancellationToken cancellationToken
    ) => await _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

    public async Task<MediaSourceInfo> GetLiveStreamMediaInfo(
        string id,
        CancellationToken cancellationToken
    ) => await _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol) =>
        _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(
        BaseItem item,
        MediaSourceInfo source,
        User user
    ) => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(
        MediaSourceInfo mediaSource,
        bool isAudio,
        string cacheKey,
        bool addProbeDelay,
        bool isLiveStream,
        CancellationToken cancellationToken
    ) =>
        _inner.AddMediaInfoWithProbe(
            mediaSource,
            isAudio,
            cacheKey,
            addProbeDelay,
            isLiveStream,
            cancellationToken
        );
}
