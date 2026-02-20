using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities; // User
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Gelato.Decorators {
    public sealed class DtoServiceDecorator(IDtoService inner) : IDtoService {
        public double? GetPrimaryImageAspectRatio(BaseItem item) =>
            inner.GetPrimaryImageAspectRatio(item);

        public BaseItemDto GetBaseItemDto(
            BaseItem item,
            DtoOptions options,
            User? user = null,
            BaseItem? owner = null
        ) {
            var dto = inner.GetBaseItemDto(item, options, user, owner);
            Patch(dto, false);
            return dto;
        }

        public IReadOnlyList<BaseItemDto> GetBaseItemDtos(
            IReadOnlyList<BaseItem> items,
            DtoOptions options,
            User? user = null,
            BaseItem? owner = null
        ) {
            // im going to hell for this
            var item = items.FirstOrDefault();

            if (item != null && item.GetBaseItemKind() == BaseItemKind.BoxSet) {
                options.EnableUserData = false;
            }

            var list = inner.GetBaseItemDtos(items, options, user, owner);
            foreach (var itemDto in list)
            {
                Patch(itemDto, true);
            }
            return list;
        }

        public BaseItemDto GetItemByNameDto(
            BaseItem item,
            DtoOptions options,
            List<BaseItem>? taggedItems,
            User? user = null
        ) {
            var dto = inner.GetItemByNameDto(item, options, taggedItems, user);
            Patch(dto, false);
            return dto;
        }

        private void Patch(
            BaseItemDto dto,
            bool isList
        )
        {
            // mark if placeholder
            if (isList
                || dto.MediaSources?.Length != 1
                || dto.Path is null
                || !dto.MediaSources[0]
                    .Path.StartsWith("gelato", StringComparison.OrdinalIgnoreCase)) return;
            dto.LocationType = LocationType.Virtual;
            dto.Path = null;
            dto.CanDownload = false;
        }
    }
}
