using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Persistence.Durability;

/// <summary>
/// Recovers dormant ("globally owned", i.e. <c>owner_id = 0</c>) inbox messages for a *single* listening
/// endpoint, completely outside of the <c>DurabilityAgent</c>. This exists for endpoints whose listener is
/// only ever active on one node — <see cref="ListenerScope.Exclusive"/> and
/// <see cref="ListenerScope.PinnedToLeader"/> — where the per-database durability agent may well be assigned
/// to a *different* node than the one hosting the listener, and inbox recovery is gated on the *local*
/// listener circuit being <see cref="ListeningStatus.Accepting"/>. See GH-3590.
///
/// This works purely against the <see cref="IMessageStore"/> surface, so every store (RDBMS family, RavenDb,
/// CosmosDb, and any future store) gets it for free, and it sweeps every database that could hold inbox rows
/// for the listener: the main store, every tenant database, and every ancillary store.
/// </summary>
public class ListenerInboxRecovery
{
    private readonly IListenerCircuit _circuit;
    private readonly ILogger _logger;
    private readonly IWolverineRuntime _runtime;

    public ListenerInboxRecovery(IWolverineRuntime runtime, IListenerCircuit circuit, ILogger logger)
    {
        _runtime = runtime;
        _circuit = circuit;
        _logger = logger;
    }

    /// <summary>
    /// Sweep every known message store for dormant incoming envelopes addressed to this listener, reassign
    /// them to the current node, and enqueue them directly into the listener. Returns the number of envelopes
    /// that were recovered.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken token = default)
    {
        if (_circuit.Status != ListeningStatus.Accepting)
        {
            return 0;
        }

        IReadOnlyList<IMessageStore> stores;
        try
        {
            // Note that this deliberately goes through MessageStoreCollection instead of the "Main" store so
            // that separate-database-per-tenant systems hit *every* tenant database, dynamic tenant sources are
            // refreshed on each sweep, and ancillary stores are covered too.
            stores = await _runtime.Stores.FindAllAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to determine the message stores for inbox recovery of listener {Uri}",
                _circuit.Endpoint.Uri);
            return 0;
        }

        var total = 0;
        foreach (var store in stores)
        {
            if (token.IsCancellationRequested) break;
            if (_circuit.Status != ListeningStatus.Accepting) break;

            try
            {
                total += await recoverFromStoreAsync(store, token);
            }
            catch (Exception e)
            {
                // One bad database (a tenant that just got dropped, a transient outage) must not abort the
                // sweep of the others.
                _logger.LogError(e,
                    "Error trying to recover inbox messages for listener {Uri} from message store {Store}",
                    _circuit.Endpoint.Uri, store.Name);
            }
        }

        return total;
    }

    private async Task<int> recoverFromStoreAsync(IMessageStore store, CancellationToken token)
    {
        var destination = _circuit.Endpoint.Uri;
        var total = 0;

        while (!token.IsCancellationRequested)
        {
            var pageSize = DeterminePageSize(_circuit, _runtime.DurabilitySettings);
            if (pageSize <= 0)
            {
                break;
            }

            var envelopes = await store.LoadPageOfGloballyOwnedIncomingAsync(destination, pageSize);
            if (envelopes.Count == 0)
            {
                break;
            }

            // Ensure each recovered envelope carries a reference to the store it was loaded from. This is
            // critical for ancillary stores: without it the envelope's Store property is null and
            // DelegatingMessageInbox falls back to the main store when marking the envelope as handled --
            // leaving it stuck as "Incoming" in the ancillary store. See GH-2318.
            foreach (var envelope in envelopes)
            {
                envelope.Store ??= store;
            }

            await store.ReassignIncomingAsync(_runtime.DurabilitySettings.AssignedNodeNumber, envelopes);
            await _circuit.EnqueueDirectlyAsync(envelopes);

            _logger.RecoveredIncoming(envelopes);
            _logger.LogInformation(
                "Recovered {Count} messages from the inbox of {Store} for single node listener {Listener}",
                envelopes.Count, store.Name, destination);

            total += envelopes.Count;

            if (envelopes.Count < pageSize)
            {
                break;
            }

            if (_circuit.Status != ListeningStatus.Accepting)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// Mirrors <see cref="RecoverIncomingMessagesCommand.DeterminePageSize"/> -- a latched or too busy listener
    /// recovers nothing, and the page never pushes the listener past its buffering limits.
    /// </summary>
    internal static int DeterminePageSize(IListenerCircuit listener, DurabilitySettings settings)
    {
        if (listener.Status != ListeningStatus.Accepting)
        {
            return 0;
        }

        var pageSize = settings.RecoveryBatchSize;

        if (pageSize + listener.QueueCount > listener.Endpoint.BufferingLimits.Maximum)
        {
            pageSize = listener.Endpoint.BufferingLimits.Maximum - listener.QueueCount - 1;
        }

        return pageSize < 0 ? 0 : pageSize;
    }
}

/// <summary>
/// Background poller that drives <see cref="ListenerInboxRecovery"/> for a durable listening endpoint whose
/// listener is only active on one node. A single sweep at startup is not sufficient: orphan release for a dead
/// node (<c>owner_id</c> back to 0) happens later, on whichever node holds that database's durability agent, so
/// dormant rows can appear *after* the exclusive listener has already restarted on its new node. Polling also
/// naturally picks up newly provisioned tenant databases.
/// </summary>
internal class ListenerInboxRecoveryLoop : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly IListenerCircuit _circuit;
    private readonly ILogger _logger;
    private readonly ListenerInboxRecovery _recovery;
    private readonly TimeSpan _pollingTime;
    private readonly Task _task;

    public ListenerInboxRecoveryLoop(IWolverineRuntime runtime, IListenerCircuit circuit, ILogger logger)
    {
        _circuit = circuit;
        _logger = logger;
        _recovery = new ListenerInboxRecovery(runtime, circuit, logger);
        _pollingTime = runtime.DurabilitySettings.ScheduledJobPollingTime;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(runtime.DurabilitySettings.Cancellation);

        _task = Task.Run(() => pollAsync(_cancellation.Token), _cancellation.Token);
    }

    private async Task pollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_circuit.Status != ListeningStatus.Accepting)
            {
                // The circuit is latched, paused, or stopped. Keep the loop alive so a Restarter-driven
                // recovery of the *listener* resumes inbox recovery without any extra wiring.
                if (!await delayAsync(token)) return;
                continue;
            }

            try
            {
                await _recovery.RecoverAsync(token);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while recovering inbox messages for single node listener {Uri}",
                    _circuit.Endpoint.Uri);
            }

            if (!await delayAsync(token)) return;
        }
    }

    private async Task<bool> delayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_pollingTime, token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.SafeDispose();
        _task.SafeDispose();
    }
}
