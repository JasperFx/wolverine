using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Wolverine.Persistence.Durability;

internal enum RecoveryDirection
{
    Incoming,
    Outgoing
}

/// <summary>
/// GH-5587. Durable recovery works on one lane per destination, so a destination that is slow to take what is
/// recovered for it (a sequential listener whose handler is still busy, a broker that is not accepting sends) never
/// holds up recovery for any other destination, and incoming recovery never holds up outgoing recovery.
/// </summary>
/// <remarks>
/// <para>A destination has at most one lane running in each direction. While it runs, recovery skips that destination,
/// so nothing more is claimed for a destination that has not taken what it already has: one page per destination at
/// most is held in memory, and the rest stays in storage.</para>
/// <para>Reading and claiming a page is limited to <see cref="MaximumConcurrentClaims"/> lanes at once. Waiting for the
/// destination to take the page is not, so destinations that are blocked never take the place of ones that are not.
/// Nothing is kept for a destination once its lane finishes.</para>
/// </remarks>
internal sealed class RecoveryLanes
{
    /// <summary>
    /// How many lanes may read and claim from the message stores at once. The same as the parallelism of the agents
    /// queue that ran every recovery command before GH-5587
    /// </summary>
    public const int MaximumConcurrentClaims = 20;

    private readonly ConcurrentDictionary<(RecoveryDirection Direction, Uri Destination), Task> _lanes = new();
    private readonly SemaphoreSlim _claims = new(MaximumConcurrentClaims, MaximumConcurrentClaims);
    private readonly ILogger _logger;

    public RecoveryLanes(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The lanes running right now
    /// </summary>
    public int Count => _lanes.Count;

    public bool IsRunning(RecoveryDirection direction, Uri destination)
    {
        return _lanes.ContainsKey((direction, destination));
    }

    /// <summary>
    /// Start recovery work for one destination in the background, unless that destination already has a lane running
    /// in this direction. Answers false, and runs nothing, in that case.
    /// </summary>
    public bool TryStart(RecoveryDirection direction, Uri destination, Func<Task> work)
    {
        var key = (direction, destination);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_lanes.TryAdd(key, finished.Task))
        {
            return false;
        }

        _ = runAsync(key, work, finished);
        return true;
    }

    private async Task runAsync((RecoveryDirection Direction, Uri Destination) key, Func<Task> work,
        TaskCompletionSource finished)
    {
        try
        {
            // Never run the work on the thread that started it: the recovery loop moves on to the next destination
            await Task.Yield();
            await work().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error recovering {Direction} messages for {Destination}", key.Direction,
                key.Destination);
        }
        finally
        {
            _lanes.TryRemove(new KeyValuePair<(RecoveryDirection, Uri), Task>(key, finished.Task));
            finished.TrySetResult();
        }
    }

    /// <summary>
    /// Read and claim a page from a message store, waiting first if <see cref="MaximumConcurrentClaims"/> lanes are
    /// already doing that
    /// </summary>
    public async Task<T> ClaimAsync<T>(Func<Task<T>> claim, CancellationToken token)
    {
        await _claims.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await claim().ConfigureAwait(false);
        }
        finally
        {
            _claims.Release();
        }
    }

    /// <summary>
    /// Wait for every lane running right now to finish. For tests and diagnostics
    /// </summary>
    public Task WaitForIdleAsync()
    {
        return Task.WhenAll(_lanes.Values.ToArray());
    }
}
