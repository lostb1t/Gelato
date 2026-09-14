namespace Gelato.Tests.Common;

public class KeyLockTests
{
    [Fact]
    public async Task SingleFlight_SameKey_RunsActionOnceAndSharesTheTask()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var runs = 0;

        Task Action(CancellationToken _)
        {
            Interlocked.Increment(ref runs);
            return gate.Task;
        }

        var first = keyLock.RunSingleFlightAsync(key, Action);
        var second = keyLock.RunSingleFlightAsync(key, Action);

        Assert.Same(first, second);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task SingleFlight_AfterCompletion_RunsAgain()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var runs = 0;

        Task Action(CancellationToken _)
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        }

        await keyLock.RunSingleFlightAsync(key, Action);
        await keyLock.RunSingleFlightAsync(key, Action);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task SingleFlight_DifferentKeys_RunIndependently()
    {
        var keyLock = new KeyLock();
        var runs = 0;

        Task Action(CancellationToken _)
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        }

        await Task.WhenAll(
            keyLock.RunSingleFlightAsync(Guid.NewGuid(), Action),
            keyLock.RunSingleFlightAsync(Guid.NewGuid(), Action)
        );

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Queued_SameKey_NeverOverlaps()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var inside = 0;
        var maxInside = 0;
        var sync = new object();

        async Task Action(CancellationToken _)
        {
            var now = Interlocked.Increment(ref inside);
            lock (sync)
            {
                maxInside = Math.Max(maxInside, now);
            }
            await Task.Delay(15);
            Interlocked.Decrement(ref inside);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => keyLock.RunQueuedAsync(key, Action)));

        Assert.Equal(1, maxInside);
    }

    [Fact]
    public async Task Queued_ReleasesLock_WhenActionThrows()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            keyLock.RunQueuedAsync(key, _ => throw new InvalidOperationException("boom"))
        );

        // If the semaphore leaked, this would hang; the timeout turns a hang into a failure.
        var second = keyLock.RunQueuedAsync(key, _ => Task.CompletedTask);
        var completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(second, completed);
    }
}
