using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities; // User
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Gelato.Decorators
{
    public sealed class DtoServiceDecorator : IDtoService
    {
        private readonly IDtoService _inner;
        private readonly Lazy<GelatoManager> _manager;

        public DtoServiceDecorator(IDtoService inner, Lazy<GelatoManager> manager)
        {
            _inner = inner;
            _manager = manager;
        }

        public double? GetPrimaryImageAspectRatio(BaseItem item) =>
            _inner.GetPrimaryImageAspectRatio(item);

        public BaseItemDto GetBaseItemDto(
            BaseItem item,
            DtoOptions options,
            User? user = null,
            BaseItem? owner = null
        )
        {
            var dto = _inner.GetBaseItemDto(item, options, user, owner);
            Patch(dto, item, user, owner, options, false);
            return dto;
        }

        public IReadOnlyList<BaseItemDto> GetBaseItemDtos(
            IReadOnlyList<BaseItem> items,
            DtoOptions options,
            User? user = null,
            BaseItem? owner = null
        )
        {
            var list = _inner.GetBaseItemDtos(items, options, user, owner);
            for (int i = 0; i < list.Count; i++)
            {
                Patch(list[i], item: null, user, owner, options, true);
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
            var dto = _inner.GetItemByNameDto(item, options, taggedItems, user);
            Patch(dto, item, user, owner: null, options, false);
            return dto;
        }

        // Not bulletproof, but providerIds are often not available
        static bool IsStremio(BaseItemDto dto)
        {
            return dto.LocationType == LocationType.Remote
                && (
                    dto.Type == BaseItemKind.Movie
                    || dto.Type == BaseItemKind.Episode
                    || dto.Type == BaseItemKind.Series
                    || dto.Type == BaseItemKind.Season
                );
        }

        private void Patch(
            BaseItemDto dto,
            BaseItem? item,
            User? user,
            BaseItem? owner,
            DtoOptions options,
            bool IsList
        )
        {
            var manager = _manager.Value;

            //dto.MediaSourceCount = 1;
            //dto.Container = null;

            if (
                item is not null
                && user is not null
                && IsStremio(dto)
                && manager.CanDelete(item, user)
            )
            {
               // dto.CanDelete = true;
            }
            if (IsStremio(dto))
            {
             //   dto.CanDownload = true;

                // clean name (legacy)
                var parts = dto.Name.Split(":::");
                dto.Name = parts.Length > 1 ? parts[1] : dto.Name;

                // mark unplayable
                if (
                    !IsList
                        && dto.MediaSources?.Length == 1
                        && dto.Path is not null
                        && dto.Path.StartsWith("stremio", StringComparison.OrdinalIgnoreCase)
                    || (!IsList && item is not null && item.IsUnaired)
                )
                {
                    dto.LocationType = LocationType.Virtual;
                    dto.Path = null;
                }
            }
        }
    }
}
