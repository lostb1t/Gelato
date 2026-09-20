using Jellyfin.Data.Enums;
using MediaBrowser.Model.MediaInfo;
using Jellyfin.Database.Implementations.Entities; // User
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Gelato.Decorators;

public sealed class DtoServiceDecorator(
    IDtoService inner,
    Lazy<GelatoManager> manager,
    IHttpContextAccessor http,
    ILibraryManager libraryManager,
    IUserDataManager userDataManager
) : IDtoService
{
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly IHttpContextAccessor _http = http;

    public double? GetPrimaryImageAspectRatio(BaseItem item) =>
        inner.GetPrimaryImageAspectRatio(item);

    public BaseItemDto GetBaseItemDto(
        BaseItem item,
        DtoOptions options,
        User? user = null,
        BaseItem? owner = null
    )
    {
        var dto = inner.GetBaseItemDto(item, options, user, owner);
        AddPrimaryVersionFields(dto, item, options, user);
        CountStreamsAsOneSource(dto, item);
        Patch(dto, item, _http.HttpContext?.IsApiListing() == true, user);
        return dto;
    }

    /// <summary>
    /// A stream row is a version of its movie/episode, and clients show it as the page item when it
    /// is picked. Give it the movie's images, cast and tags: rows store no images or people, and
    /// their only tag marks them as stream rows. Image requests for a row are served from the movie
    /// by ImageResourceFilter. The watch state is the movie's too: StreamUserDataSync copies what is
    /// saved on a stream to the movie, and rows linked or added later hold none of it.
    /// </summary>
    private void AddPrimaryVersionFields(
        BaseItemDto dto,
        BaseItem item,
        DtoOptions options,
        User? user
    )
    {
        if (
            !item.HasStreamTag()
            || (item as Video)?.PrimaryVersionId is not { } primaryId
            || libraryManager.GetItemById(primaryId) is not { } primary
        )
        {
            return;
        }

        if (options.ContainsField(ItemFields.Tags))
        {
            dto.Tags = primary.Tags;
        }

        if (
            dto.UserData is { } userData
            && user is not null
            && userDataManager.GetUserDataDto(primary, user) is { } primaryData
        )
        {
            userData.Played = primaryData.Played;
            userData.PlayCount = primaryData.PlayCount;
            userData.PlaybackPositionTicks = primaryData.PlaybackPositionTicks;
            userData.PlayedPercentage = primaryData.PlayedPercentage;
            userData.LastPlayedDate = primaryData.LastPlayedDate;
            userData.IsFavorite = primaryData.IsFavorite;
            userData.Likes = primaryData.Likes;
            userData.Rating = primaryData.Rating;
        }

        var addPeople = dto.People is not { Length: > 0 } && options.ContainsField(ItemFields.People);
        if (!options.EnableImages && !addPeople)
        {
            return;
        }

        List<ItemFields> fields = [ItemFields.PrimaryImageAspectRatio];
        if (addPeople)
        {
            fields.Add(ItemFields.People);
        }

        var primaryDto = inner.GetBaseItemDto(
            primary,
            new DtoOptions(false)
            {
                Fields = fields,
                EnableImages = options.EnableImages,
                ImageTypes = options.ImageTypes,
                ImageTypeLimit = options.ImageTypeLimit,
                EnableUserData = false,
            },
            user
        );

        if (addPeople)
        {
            dto.People = primaryDto.People;
        }

        if (options.EnableImages)
        {
            dto.ImageTags = primaryDto.ImageTags;
            dto.BackdropImageTags = primaryDto.BackdropImageTags;
            dto.ImageBlurHashes = primaryDto.ImageBlurHashes;
            if (options.ContainsField(ItemFields.PrimaryImageAspectRatio))
            {
                dto.PrimaryImageAspectRatio = primaryDto.PrimaryImageAspectRatio;
            }
        }
    }

    /// <summary>
    /// Stream rows are linked as versions, so Jellyfin counts them into MediaSourceCount and
    /// clients badge every movie card with the number of streams. Count them as the one source
    /// they stand in for, like before they were versions.
    /// </summary>
    private void CountStreamsAsOneSource(BaseItemDto dto, BaseItem item)
    {
        if (dto.MediaSourceCount is not > 1 || item is not Video video)
        {
            return;
        }

        // A version reports its movie's count, like Jellyfin does. The links are counted in the
        // database, as Jellyfin does: search results are built from new instances whose links are
        // empty. Versions merged in by hand still count.
        var owner = video.PrimaryVersionId is { } primaryId
            ? libraryManager.GetItemById(primaryId) as Video
            : video;
        var streams = owner is null
            ? 0
            : libraryManager.GetLinkedAlternateVersions(owner).Count(v => v.HasStreamTag());
        if (streams == 0)
            return;

        var count = dto.MediaSourceCount.Value - streams;
        dto.MediaSourceCount = count > 1 ? count : null;
    }

    public IReadOnlyList<BaseItemDto> GetBaseItemDtos(
        IReadOnlyList<BaseItem> items,
        DtoOptions options,
        User? user = null,
        BaseItem? owner = null,
        bool skipVisibilityCheck = false
    )
    {
        // im going to hell for this
        var item = items.FirstOrDefault();

        if (item != null && item.GetBaseItemKind() == BaseItemKind.BoxSet)
        {
            options.EnableUserData = false;
        }

        var list = inner.GetBaseItemDtos(items, options, user, owner, skipVisibilityCheck);
        foreach (var itemDto in list)
        {
            Patch(itemDto, item, true, user);
        }
        // By id: the inner service leaves out items the user may not see, so the DTOs do not line
        // up with the items by position.
        var byId = new Dictionary<Guid, BaseItem>();
        foreach (var candidate in items)
        {
            byId.TryAdd(candidate.Id, candidate);
        }
        foreach (var itemDto in list)
        {
            if (!byId.TryGetValue(itemDto.Id, out var source))
                continue;

            AddPrimaryVersionFields(itemDto, source, options, user);
            CountStreamsAsOneSource(itemDto, source);
        }
        return list;
    }

    public BaseItemDto GetItemByNameDto(
        BaseItem item,
        DtoOptions options,
        List<BaseItem>? taggedItems,
        User? user = null
    )
    {
        var dto = inner.GetItemByNameDto(item, options, taggedItems, user);
        Patch(dto, item, _http.HttpContext?.IsApiListing() == true, user);
        return dto;
    }

    static bool IsGelato(BaseItemDto dto)
    {
        return dto.LocationType == LocationType.Remote
            && (
                dto.Type == BaseItemKind.Movie
                || dto.Type == BaseItemKind.Episode
                || dto.Type == BaseItemKind.Series
                || dto.Type == BaseItemKind.Season
            );
    }

    private void Patch(BaseItemDto dto, BaseItem? item, bool isList, User? user)
    {
        var manager = _manager.Value;
        if (item is not null && user is not null && IsGelato(dto) && manager.CanDelete(item, user))
        {
            dto.CanDelete = true;
        }

        if (IsGelato(dto))
        {
            dto.CanDownload = true;
            // mark if placeholder
            if (
                isList
                || dto.MediaSources?.Length != 1
                || dto.Path is null
                || !dto.MediaSources[0]
                    .Path.StartsWith("gelato", StringComparison.OrdinalIgnoreCase)
            )
            {
                StubStreamPaths(dto);
                return;
            }

            dto.LocationType = LocationType.Virtual;
            dto.Path = null;
            dto.CanDownload = false;
            StubStreamPaths(dto);
        }
    }

    /// <summary>
    /// Takes the stream paths out of the DTO. A stream row's own path and the paths of a movie's
    /// or an episode's media sources are addon URLs with the debrid API key in them, so any client
    /// asking for Fields=MediaSources, and anything that logs or caches that answer, would get the
    /// key; the host in them is often one only the server can reach, and a client that trusts
    /// SupportsDirectPlay from the DTO instead of from PlaybackInfo tries it and times out.
    /// Clients get a stub they can only play through Jellyfin. MediaSourceManagerDecorator keeps
    /// the real URL on Jellyfin's own playback path.
    /// </summary>
    private static void StubStreamPaths(BaseItemDto dto)
    {
        if (dto.Path is not null && dto.Path.IsUrl())
        {
            dto.Path = "/stub";
        }

        foreach (var source in dto.MediaSources ?? [])
        {
            source.Stub();
        }
    }
}
