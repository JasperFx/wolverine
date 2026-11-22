using System.Data.Common;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Transport;

/// <summary>
/// Base class for the database-backed interop transport listeners (NServiceBus / MassTransit over
/// SQL Server or PostgreSQL). Owns the common polling loop: a cancellable background task that
/// repeatedly pops a batch of messages from a queue table/function, forwards them to the receiver,
/// and backs off on idle or error. Subclasses supply only the dialect-specific batch pop
/// (<see cref="TryPopAsync"/>) and the ack / nack behaviour (<see cref="CompleteAsync"/> /
/// <see cref="DeferAsync"/>).
/// </summary>
public abstract class DatabaseListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IReceiver _receiver;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly string _queueName;
    private Task? _task;

    /// <summary>
    /// GH-5815. The receive transaction holding the one message in flight, when the listener receives in one
    /// </summary>
    private HeldReceive? _held;

    protected DatabaseListener(Uri address, string queueName, IReceiver receiver, ILogger logger,
        TimeSpan pollingInterval)
    {
        Address = address;
        _queueName = queueName;
        _receiver = receiver;
        _logger = logger;
        _pollingInterval = pollingInterval;
    }

    public Uri Address { get; }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    /// <summary>The cancellation token signalled when the listener is stopped.</summary>
    protected CancellationToken Cancellation => _cancellation.Token;

    /// <summary>The maximum number of messages to fetch in a single poll.</summary>
    protected abstract int MaximumMessagesToReceive { get; }

    /// <summary>Pop up to <paramref name="maximumMessages"/> messages from the database queue.</summary>
    protected abstract Task<IReadOnlyList<Envelope>> TryPopAsync(int maximumMessages, CancellationToken token);

    public abstract ValueTask CompleteAsync(Envelope envelope);

    public abstract ValueTask DeferAsync(Envelope envelope);

    /// <summary>
    /// GH-5775. Backoff between attempts to put a popped message back into its queue table. The last value
    /// repeats until the time budget runs out.
    /// </summary>
    internal static readonly TimeSpan[] ReinsertBackoff =
    [
        100.Milliseconds(), 250.Milliseconds(), 500.Milliseconds(), 1.Seconds(), 2.Seconds()
    ];

    /// <summary>
    /// GH-5775. For a listener whose receive is a destructive pop, the row is already gone by the time the
    /// message is deferred, so the re-insert is the only copy of the message there will ever be. Runs
    /// <paramref name="reinsert"/> until it succeeds, backing off between attempts, until
    /// <paramref name="budget"/> has elapsed or <paramref name="cancellation"/> is signalled. A final failure
    /// throws a <see cref="DatabaseQueueReinsertFailedException"/> naming the message instead of returning.
    /// </summary>
    /// <param name="envelope">The message being put back</param>
    /// <param name="reinsert">
    /// One insert attempt. The argument is the 1-based attempt number; an attempt after the first must be
    /// idempotent, because the attempt before it may have committed even though it reported a failure.
    /// </param>
    /// <param name="budget">How long to keep retrying, typically the host's drain timeout</param>
    /// <param name="cancellation">The host's cancellation</param>
    protected async ValueTask reinsertWithRetriesAsync(Envelope envelope, Func<int, Task> reinsert,
        TimeSpan budget, CancellationToken cancellation)
    {
        var started = DateTimeOffset.UtcNow;
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                await reinsert(attempt);
                return;
            }
            catch (Exception e)
            {
                var delay = ReinsertBackoff[Math.Min(attempt - 1, ReinsertBackoff.Length - 1)];
                var outOfTime = DateTimeOffset.UtcNow - started + delay > budget;

                if (outOfTime || cancellation.IsCancellationRequested)
                {
                    throw new DatabaseQueueReinsertFailedException(envelope.Id, _queueName, attempt, e);
                }

                _logger.LogWarning(e,
                    "Attempt {Attempt} to put message {EnvelopeId} back into database queue {Name} failed, retrying",
                    attempt, envelope.Id, _queueName);

                try
                {
                    await Task.Delay(delay, cancellation);
                }
                catch (OperationCanceledException)
                {
                    throw new DatabaseQueueReinsertFailedException(envelope.Id, _queueName, attempt, e);
                }
            }
        }
    }

    /// <summary>
    /// GH-5815. Hold the open receive transaction that popped <paramref name="envelope"/>. The row stays deleted
    /// but uncommitted, and so locked away from every other consumer, until <see cref="commitReceiveAsync"/> or
    /// <see cref="rollbackReceiveAsync"/> settles it. The listener owns <paramref name="connection"/> and
    /// <paramref name="transaction"/> from here on. One message at a time: call it only with a batch of one.
    /// </summary>
    protected void holdReceive(Envelope envelope, DbConnection connection, DbTransaction transaction)
    {
        var previous = Interlocked.Exchange(ref _held, new HeldReceive(envelope, connection, transaction));
        if (previous != null)
        {
            // Never expected: the polling loop settles every receive before it pops again
            _logger.LogError("A receive transaction for message {EnvelopeId} from database queue {Name} was still open when the next message was popped; rolling it back",
                previous.Envelope.Id, _queueName);
            _ = previous.SettleAsync(false);
        }
    }

    /// <summary>
    /// GH-5815. Commit the receive transaction holding <paramref name="envelope"/>, which deletes its row for good.
    /// False when no receive transaction holds it.
    /// </summary>
    protected async ValueTask<bool> commitReceiveAsync(Envelope envelope)
    {
        var held = take(envelope);
        if (held == null) return false;

        await held.SettleAsync(true);
        return true;
    }

    /// <summary>
    /// GH-5815. Roll back the receive transaction holding <paramref name="envelope"/>, which puts its row back
    /// exactly as it was, in its original place in the queue. False when no receive transaction holds it.
    /// </summary>
    protected async ValueTask<bool> rollbackReceiveAsync(Envelope envelope)
    {
        var held = take(envelope);
        if (held == null) return false;

        await held.SettleAsync(false);
        return true;
    }

    private HeldReceive? take(Envelope envelope)
    {
        var held = Volatile.Read(ref _held);
        if (held == null || !ReferenceEquals(held.Envelope, envelope)) return null;

        return Interlocked.CompareExchange(ref _held, null, held) == held ? held : null;
    }

    /// <summary>
    /// GH-5815. Settle a receive transaction that is still open once the receiver is done with the message. The
    /// receiver settles a message through CompleteAsync or DeferAsync; one that does neither and returns normally
    /// was handled some other way (a retry rescheduled into the message store, for one) and is committed, as the
    /// destructive pop would have left it. A receiver that throws has not handled it, so it is rolled back.
    /// </summary>
    private async Task settleLeftoverReceiveAsync(bool handled)
    {
        var held = Interlocked.Exchange(ref _held, null);
        if (held == null) return;

        try
        {
            await held.SettleAsync(handled);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error settling the receive transaction for message {EnvelopeId} from database queue {Name}",
                held.Envelope.Id, _queueName);
        }
    }

    public Task StartAsync()
    {
        _task = Task.Run(listenForMessagesAsync, _cancellation.Token);
        return Task.CompletedTask;
    }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        _task?.SafeDispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        // GH-5815. A message still in flight when the listener is torn down (the drain timed out) is handed back
        // to the queue, where another consumer can take it, rather than held until its handler finishes
        var held = Interlocked.Exchange(ref _held, null);
        if (held != null)
        {
            try
            {
                await held.SettleAsync(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error rolling back the receive transaction for message {EnvelopeId} from database queue {Name}",
                    held.Envelope.Id, _queueName);
            }
        }
    }

    private async Task listenForMessagesAsync()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var messages = await TryPopAsync(MaximumMessagesToReceive, _cancellation.Token);
                failedCount = 0;

                if (messages.Count > 0)
                {
                    var handled = false;
                    try
                    {
                        await _receiver.ReceivedAsync(this, messages.ToArray());
                        handled = true;
                    }
                    finally
                    {
                        await settleLeftoverReceiveAsync(handled);
                    }
                }
                else
                {
                    await Task.Delay(_pollingInterval, _cancellation.Token);
                }
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException or OperationCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }

                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();
                _logger.LogError(e, "Error while trying to receive messages from database queue {Name}", _queueName);
                await Task.Delay(pauseTime);
            }
        }
    }

    /// <summary>
    /// GH-5815. An open receive transaction and the connection it lives on, settled exactly once
    /// </summary>
    private sealed class HeldReceive(Envelope envelope, DbConnection connection, DbTransaction transaction)
    {
        public Envelope Envelope { get; } = envelope;

        public async Task SettleAsync(bool commit)
        {
            try
            {
                // Never the listener's cancellation: a stop must not abandon the commit of a handled message
                if (commit)
                {
                    await transaction.CommitAsync(CancellationToken.None);
                }
                else
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
            }
            finally
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }
}
