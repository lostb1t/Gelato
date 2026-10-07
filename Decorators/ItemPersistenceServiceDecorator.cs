using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;

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
/// </remarks>
public sealed class ItemPersistenceServiceDecorator : IItemPersistenceService
{
    private readonly IItemPersistenceService _inner;
    private readonly ItemWriteCounter _writes;

    public ItemPersistenceServiceDecorator(IItemPersistenceService inner, ItemWriteCounter writes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _writes.StartCounting();
    }

    public void DeleteItem(params IReadOnlyList<Guid> ids)
    {
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
