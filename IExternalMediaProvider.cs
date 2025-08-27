namespace Jellyfin.Plugin.ExternalMedia;

public interface IExternalMetaProvider
{
    Task<StremioMeta?> GetMetaAsync(MediaBrowser.Controller.Entities.BaseItem entity, CancellationToken ct);
    Task<List<StremioStream>> GetStreamsAsync(MediaBrowser.Controller.Entities.BaseItem entity, CancellationToken ct);
}