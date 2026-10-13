using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

/// <summary>
/// Counts the writes to the item tables, so that what was read from them can be kept until the
/// next one.
/// </summary>
public sealed class ItemWriteCounter
{
    private long _version;
    private volatile bool _counting;

    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Whether anything reports the writes. Until <see cref="ItemPersistenceServiceDecorator"/>
    /// is in place the count says nothing, and nothing may be kept under it.
    /// </summary>
    public bool IsCounting => _counting;

    public void StartCounting() => _counting = true;

    public void Bump() => Interlocked.Increment(ref _version);
}

/// <summary>
/// Bumps <see cref="ItemWriteCounter"/> around every save and delete of an item.
/// </summary>
/// <remarks>
/// Every item write of the server ends here: Jellyfin's library manager saves and deletes through
/// this service, and Gelato saves its seasons, episodes and stream rows through it directly,
/// which raises no library event. The counter is bumped before the write and again after it: a
/// reader that took the count before the write committed never has its answer taken for one of
/// after it, and one that read while the write ran is thrown away by the second bump.
/// Images and user data are left out. They change neither an item's dates, ids, tags nor type.
/// A delete is also where Gelato's items are kept out of the removal of a library folder
/// (<see cref="GelatoManager.RescueFromRemovedFolders"/>): Jellyfin has no step before it.
/// </remarks>
public sealed class ItemPersistenceServiceDecorator : IItemPersistenceService
{
    private readonly IItemPersistenceService _inner;
    private readonly ItemWriteCounter _writes;
    private readonly Lazy<GelatoManager> _manager;
    private readonly ILogger<ItemPersistenceServiceDecorator> _log;

    public ItemPersistenceServiceDecorator(
        IItemPersistenceService inner,
        ItemWriteCounter writes,
        Lazy<GelatoManager> manager,
        ILogger<ItemPersistenceServiceDecorator> log
    )
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _writes.StartCounting();
    }

    public void DeleteItem(params IReadOnlyList<Guid> ids)
    {
        // Nothing left to delete once Gelato's items are out of it.
        var rest = WithoutRescued(ids);
        if (ids is { Count: > 0 } && rest.Count == 0)
            return;

        ids = rest;

        _writes.Bump();
        try
        {
            _inner.DeleteItem(ids);
        }
        finally
        {
            _writes.Bump();
        }
    }

    private IReadOnlyList<Guid> WithoutRescued(IReadOnlyList<Guid> ids)
    {
        // Before the plugin is loaded there is no configuration to find a folder with.
        if (ids is null || ids.Count == 0 || GelatoPlugin.Instance is null)
            return ids!;

        try
        {
            return _manager.Value.RescueFromRemovedFolders(ids);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not check a deletion for Gelato's items");
            return ids;
        }
    }

    public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
    {
        _writes.Bump();
        try
        {
            _inner.SaveItems(items, cancellationToken);
        }
        finally
        {
            _writes.Bump();
        }
    }

    public void UpdateInheritedValues()
    {
        _writes.Bump();
        try
        {
            _inner.UpdateInheritedValues();
        }
        finally
        {
            _writes.Bump();
        }
    }

    public Task SaveImagesAsync(BaseItem item, CancellationToken cancellationToken = default) =>
        _inner.SaveImagesAsync(item, cancellationToken);

    public Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken) =>
        _inner.ReattachUserDataAsync(item, cancellationToken);
}
