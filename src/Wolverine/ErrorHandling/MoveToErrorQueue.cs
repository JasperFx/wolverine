using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Util;

namespace Wolverine.ErrorHandling;

internal class MoveToErrorQueueSource : IContinuationSource
{
    public string Description => "Move to error queue";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return new MoveToErrorQueue(ex);
    }
}

internal class MoveToErrorQueue : IContinuation
{
    public MoveToErrorQueue(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public Exception Exception { get; }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        // GH-5521: moving a message to the error queue no longer sends a failure acknowledgement to the
        // sender. WolverineOptions.EnableAutomaticFailureAcks, off by default since 4.6, was removed in 7.0.
        // Nothing here reads Envelope.Destination, so a malformed system envelope without one (GH-3013)
        // is safe too.
        if (lifecycle.Envelope!.Message != null)
        {
            lifecycle.Envelope.MessageType = lifecycle.Envelope.Message.GetType().ToMessageTypeName();
        }
        else
        {
            lifecycle.Envelope.MessageType ??= $"unknown/{Exception.GetType().Name}";
        }

        // GH-5554: where the faults go is worked out before the dead-letter move, and those for a durable
        // destination are written in its transaction when the store can do that
        var faults = await routeFaultsAsync(lifecycle, runtime, activity);
        var deadLetterFaults = faults is null ? null : await prepareAsync(faults, runtime);
        if (deadLetterFaults != null) lifecycle.Envelope.DeadLetterFaults = deadLetterFaults;

        try
        {
            await lifecycle.MoveToDeadLetterQueueAsync(Exception);
        }
        catch (DeadLetterMoveFailedException e)
        {
            // The transport could not move the envelope and left the delivery with the broker, which delivers it
            // again. Nothing was dead lettered, so no fault, no completion -- that would settle, and so lose, the
            // only copy -- and no error queue record
            runtime.Logger.LogWarning(e,
                "Could not move envelope {EnvelopeId} to the error queue; the transport left it to be delivered again",
                lifecycle.Envelope.Id);
            lifecycle.CompletionTrackerFor(runtime).Requeued(lifecycle.Envelope);
            return;
        }
        finally
        {
            lifecycle.Envelope.DeadLetterFaults = null;
        }

        if (faults is null)
        {
            // Not a message context: auto-publish Fault<T> if opted in, best-effort, after the move. Never throws.
            await runtime.PublishFaultIfEnabledAsync(lifecycle, Exception, FaultTrigger.MovedToErrorQueue, activity);
        }
        else
        {
            await sendFaultsAsync(faults, deadLetterFaults, runtime);
        }

        await lifecycle.CompleteAsync();

        activity?.AddEvent(new ActivityEvent(WolverineTracing.MovedToErrorQueue));

        // GH-4136: MovedToErrorQueue only. It is reported LAST because a terminal record sweeps every
        // earlier record for the envelope complete, and the durable transports' own dead-letter move
        // emits a trailing Sent that nothing else will ever complete. The MessageFailed call that used
        // to sit alongside this is gone: two terminal records on one path cannot be ordered to satisfy
        // both that sweep and the guarantee that the ending record is present. Its dead-letter counter,
        // effective time and failure wire tap now live in MovedToErrorQueue, so no metric is lost.
        lifecycle.CompletionTrackerFor(runtime).MovedToErrorQueue(lifecycle.Envelope, Exception);
    }

    // The faults of this dead letter, routed and stamped as the fault publisher would send them, but not sent yet.
    // Null when the lifecycle is not a message context, which falls back to publishing after the move
    private async Task<List<Envelope>?> routeFaultsAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
        Activity? activity)
    {
        if (lifecycle is not MessageContext context || runtime is not IWolverineRuntimeInternal) return null;

        var captured = new List<Envelope>();
        context.CapturedOutgoing = captured;
        try
        {
            await runtime.PublishFaultIfEnabledAsync(lifecycle, Exception, FaultTrigger.MovedToErrorQueue, activity);
        }
        finally
        {
            context.CapturedOutgoing = null;
        }

        return captured;
    }

    private static async Task<DeadLetterFaults?> prepareAsync(List<Envelope> faults, IWolverineRuntime runtime)
    {
        var hooks = (runtime as WolverineRuntime)?.DeadLetterHooks;
        var failMove = hooks?.TakeFailNextMove() ?? false;

        var durable = faults.Where(x => x.Sender is { IsDurable: true }).ToArray();
        if (durable.Length == 0 && !failMove) return null;

        var deadLetterFaults = new DeadLetterFaults { FailBeforeCommit = failMove };
        try
        {
            await deadLetterFaults.PersistAsync(durable);
        }
        catch (Exception e)
        {
            // Never stop the dead-letter move over a fault: fall back to sending it after the move
            runtime.Logger.LogError(e, "Could not prepare the faults of a dead letter to be written with it");
            await deadLetterFaults.RollbackAsync();
        }

        return deadLetterFaults;
    }

    private static async Task sendFaultsAsync(List<Envelope> faults, DeadLetterFaults? written,
        IWolverineRuntime runtime)
    {
        var hooks = (runtime as WolverineRuntime)?.DeadLetterHooks;
        if (hooks != null && hooks.TakeCrashAfterNextMove())
        {
            // Test seam: the service "crashed" right after the move committed, so nothing more is sent
            hooks.Strand(faults);
            return;
        }

        if (faults.Count == 0) return;

        var persisted = written is { Written: true }
            ? written.Outgoing.Concat(written.Incoming).ToHashSet()
            : [];

        foreach (var fault in faults)
        {
            try
            {
                // Written with the dead letter: only to be sent. Otherwise stored and sent as today, best-effort
                if (persisted.Contains(fault) || fault.Sender is not { IsDurable: true })
                {
                    await fault.QuickSendAsync();
                }
                else
                {
                    await fault.StoreAndForwardAsync();
                }
            }
            catch (Exception e)
            {
                runtime.Logger.LogError(e, "Failed to send the auto Fault<T> {EnvelopeId} of a dead letter", fault.Id);
            }
        }
    }

    public override string ToString()
    {
        return "Move to Error Queue";
    }

    protected bool Equals(MoveToErrorQueue other)
    {
        return Equals(Exception, other.Exception);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((MoveToErrorQueue)obj);
    }

    public override int GetHashCode()
    {
        return Exception.GetHashCode();
    }
}