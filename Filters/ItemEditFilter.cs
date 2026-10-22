using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// One lock per item, taken by the metadata editor's save and by the refresh of a Gelato item.
/// </summary>
public sealed class ItemEditLocks
{
    private readonly Dictionary<Guid, (SemaphoreSlim Gate, int Users)> _gates = [];

    public async Task<IDisposable> EnterAsync(Guid id, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate;
        lock (_gates)
        {
            var (g, users) = _gates.TryGetValue(id, out var entry) ? entry : (new SemaphoreSlim(1, 1), 0);
            _gates[id] = (g, users + 1);
            gate = g;
        }

        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(id, false);
            throw;
        }

        return new Held(this, id);
    }

    private void Leave(Guid id, bool release)
    {
        lock (_gates)
        {
            var (gate, users) = _gates[id];
            if (release)
                gate.Release();
            if (users == 1)
                _gates.Remove(id);
            else
                _gates[id] = (gate, users - 1);
        }
    }

    private sealed class Held(ItemEditLocks locks, Guid id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                locks.Leave(id, true);
        }
    }
}

/// <summary>
/// Holds the item's <see cref="ItemEditLocks"/> lock while the metadata editor saves it.
/// </summary>
/// <remarks>
/// A series added from search refreshes its episodes one by one, each read, filled from the addon
/// and saved again. An episode the user edits while its own refresh runs got the refresh's copy
/// saved over the edit, name and lock included. The refresh holds the same lock
/// (<see cref="Decorators.ProviderManagerDecorator.RefreshSingleItem"/>), so the edit waits for it
/// and is saved last, or the refresh waits and reads the edited row.
/// </remarks>
public sealed class ItemEditFilter(ItemEditLocks locks) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        if (ctx.GetActionName() != "UpdateItem" || !ctx.TryGetRouteGuid(out var id))
        {
            await next();
            return;
        }

        using (await locks.EnterAsync(id, ctx.HttpContext.RequestAborted))
            await next();
    }
}
