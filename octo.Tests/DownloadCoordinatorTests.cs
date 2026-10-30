using Octo.Services.Common;

namespace Octo.Tests;

public class DownloadCoordinatorTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static TaskCompletionSource Gate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task SameSongConcurrentlyRunsOneDownload()
    {
        var c = new DownloadCoordinator(3);
        var runs = 0;
        var started = Gate();
        var release = Gate();

        async Task<string> Work(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            await release.Task;
            return "/music/a.flac";
        }

        var first = c.RunAsync("ext-x-1", Work, default);
        await started.Task.WaitAsync(Generous);
        var joiners = Enumerable.Range(0, 5).Select(_ => c.RunAsync("ext-x-1", Work, default)).ToArray();
        release.SetResult();

        var all = await Task.WhenAll(joiners.Append(first)).WaitAsync(Generous);
        Assert.All(all, p => Assert.Equal("/music/a.flac", p));
        Assert.Equal(1, runs);
        Assert.Equal(0, c.InFlightCount);
    }

    [Fact]
    public async Task DifferentSongsRunInParallelAndASlowOneDoesNotBlockOthers()
    {
        var c = new DownloadCoordinator(3);
        var slowStarted = Gate();
        var slowRelease = Gate();

        var slow = c.RunAsync("slow", async _ =>
        {
            slowStarted.SetResult();
            await slowRelease.Task;
            return "slow";
        }, default);
        await slowStarted.Task.WaitAsync(Generous);

        // These finish while the slow one is still held.
        var fast = await Task.WhenAll(
            c.RunAsync("a", _ => Task.FromResult("a"), default),
            c.RunAsync("b", _ => Task.FromResult("b"), default)).WaitAsync(Generous);
        Assert.Equal(new[] { "a", "b" }, fast);
        Assert.False(slow.IsCompleted);

        slowRelease.SetResult();
        Assert.Equal("slow", await slow.WaitAsync(Generous));
    }

    [Fact]
    public async Task ConcurrencyIsBoundedByTheSemaphore()
    {
        var c = new DownloadCoordinator(2);
        var active = 0;
        var peak = 0;
        var release = Gate();

        async Task<string> Work(CancellationToken ct)
        {
            var now = Interlocked.Increment(ref active);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now &&
                   Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await release.Task;
            Interlocked.Decrement(ref active);
            return "ok";
        }

        var tasks = Enumerable.Range(0, 6).Select(i => c.RunAsync($"k{i}", Work, default)).ToArray();
        var deadline = DateTime.UtcNow + Generous;
        while (Volatile.Read(ref active) < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Task.Delay(100);
        Assert.Equal(2, Volatile.Read(ref active));

        release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(Generous);
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task FailureIsSharedThenRetrySucceeds()
    {
        var c = new DownloadCoordinator(3);
        var started = Gate();
        var release = Gate();

        async Task<string> Failing(CancellationToken ct)
        {
            started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("boom");
        }

        var first = c.RunAsync("s", Failing, default);
        await started.Task.WaitAsync(Generous);
        var joiner = c.RunAsync("s", Failing, default);
        release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => joiner);
        Assert.Equal(0, c.InFlightCount);

        Assert.Equal("/music/s.flac", await c.RunAsync("s", _ => Task.FromResult("/music/s.flac"), default));
    }

    [Fact]
    public async Task CancelledOwnerIsNotRetainedAndAJoinerCanCancelItsOwnWait()
    {
        var c = new DownloadCoordinator(3);
        using var ownerCts = new CancellationTokenSource();
        var started = Gate();

        var owner = c.RunAsync("s", async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }, ownerCts.Token);
        await started.Task.WaitAsync(Generous);

        using var joinerCts = new CancellationTokenSource();
        var joiner = c.RunAsync("s", _ => Task.FromResult("x"), joinerCts.Token);
        joinerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner);
        Assert.False(owner.IsCompleted); // the joiner leaving does not stop the download

        ownerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.Equal(0, c.InFlightCount);
        Assert.Equal("again", await c.RunAsync("s", _ => Task.FromResult("again"), default));
    }
}
