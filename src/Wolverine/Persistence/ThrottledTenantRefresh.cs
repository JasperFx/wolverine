using System.Diagnostics;
using Wolverine.Persistence.Durability;

namespace Wolverine.Persistence;

/// <summary>
///     Guards the tenant database discovery behind a dynamic tenancy source: concurrent callers share one
///     refresh, and a refresh that has just succeeded is not repeated until the configured stale time has
///     elapsed.
/// </summary>
/// <remarks>
///     <para>
///         GH-4267. Discovery is a round trip to the tenant registry, and
///         <see cref="MessageStoreCollection.FindAllAsync()" /> sits on paths that are retried on failure —
///         listener inbox recovery, listener drain, the durability sweeps. Without a guard each retry and
///         each concurrent caller opens its own connection to the registry, so on a large tenant fleet the
///         thing that ran out of connections is called again by the retry for the failure it caused.
///     </para>
///     <para>
///         Freshness is not what those callers need: <see cref="MessageStoreCollection.FindDatabaseAsync" />
///         forces a refresh whenever a lookup misses, which is the path where a newly provisioned tenant
///         database has to be found right now. A bulk enumeration that is a few seconds stale costs nothing
///         — the next sweep sees the database.
///     </para>
/// </remarks>
internal sealed class ThrottledTenantRefresh
{
    private readonly object _locker = new();
    private readonly ITenantedMessageSource _source;
    private readonly Func<TimeSpan> _staleTime;

    private Task? _inFlight;
    private long _refreshedAt;
    private bool _hasRefreshed;

    public ThrottledTenantRefresh(ITenantedMessageSource source, Func<TimeSpan> staleTime)
    {
        _source = source;
        _staleTime = staleTime;
    }

    public Task MaybeRefreshAsync()
    {
        TaskCompletionSource completion;

        lock (_locker)
        {
            if (_hasRefreshed && Stopwatch.GetElapsedTime(_refreshedAt) < _staleTime())
            {
                return Task.CompletedTask;
            }

            // A refresh already under way covers this caller too. Joining it instead of starting a second
            // one is the whole point of this class.
            if (_inFlight is { IsCompleted: false })
            {
                return _inFlight;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
        }

        _ = refreshAsync(completion);

        return completion.Task;
    }

    private async Task refreshAsync(TaskCompletionSource completion)
    {
        try
        {
            await _source.RefreshAsync().ConfigureAwait(false);

            // Only a successful refresh opens the window. A failed one leaves the source unknown, so the
            // next caller retries it — one at a time, because the guard above still holds.
            lock (_locker)
            {
                _refreshedAt = Stopwatch.GetTimestamp();
                _hasRefreshed = true;
            }

            completion.SetResult();
        }
        catch (Exception e)
        {
            completion.SetException(e);
        }
    }
}
