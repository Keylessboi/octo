using System.Collections.Concurrent;

namespace Octo.Services.Common;

/// <summary>
/// Dedupes downloads per key and bounds how many run at once.
///
/// The only critical section is the check-and-insert on the in-flight map. The real work
/// runs outside it behind a separate bounded semaphore, so one slow download no longer
/// blocks unrelated ones, and callers asking for a song that is already running await the
/// same task instead of polling.
///
/// Nothing outlives the work: the entry is removed when the task finishes, success or
/// failure, so a failed download never poisons a later retry.
/// </summary>
public sealed class DownloadCoordinator
{
    private sealed class Entry
    {
        public readonly TaskCompletionSource<string> Source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, Entry> _inFlight = new();
    private readonly SemaphoreSlim _slots;

    public DownloadCoordinator(int maxConcurrent)
    {
        MaxConcurrent = Math.Max(1, maxConcurrent);
        _slots = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
    }

    public int MaxConcurrent { get; }

    public int InFlightCount => _inFlight.Count;

    /// <summary>
    /// Run <paramref name="work"/> for <paramref name="key"/>, or join the run already in
    /// flight. The owner's token drives the work; a joiner's token only stops that joiner
    /// from waiting. If the owner is cancelled or fails, joiners see the same outcome and
    /// the next caller starts fresh.
    /// </summary>
    public async Task<string> RunAsync(
        string key, Func<CancellationToken, Task<string>> work, CancellationToken ct)
    {
        var mine = new Entry();
        // GetOrAdd without a value factory is the atomic check-and-insert and runs no
        // user code inside the dictionary.
        var existing = _inFlight.GetOrAdd(key, mine);
        if (!ReferenceEquals(existing, mine))
        {
            return await existing.Source.Task.WaitAsync(ct);
        }

        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                mine.Source.SetResult(await work(ct));
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException oce)
        {
            mine.Source.TrySetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            mine.Source.TrySetException(ex);
        }
        finally
        {
            // After the outcome is set: a caller arriving in between joins a finished task.
            _inFlight.TryRemove(key, out _);
        }

        return await mine.Source.Task;
    }
}
