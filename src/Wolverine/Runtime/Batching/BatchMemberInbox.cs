using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.Runtime.Batching;

/// <summary>
/// F3 / B2. Settles the inbox rows of batch members whose batch executes on a BUFFERED local queue. A member that
/// arrived on a durable listener has its own inbox row, owned by this node, but the buffered queue its batch runs on
/// has no inbox of its own: its terminal used to settle the members' pending counts and nothing else, so every
/// member stayed Incoming and ran again after the next restart or stale sweep. This writes to the inbox that holds
/// each member's row, routed by the member's own store the same way <see cref="WorkerQueues.DurableReceiver"/>
/// routes it, and keeps trying until the store comes back or the host shuts down.
/// </summary>
internal class BatchMemberInbox : IDisposable
{
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;
    private readonly RetryBlock<Envelope[]> _markAsHandled;
    private readonly RetryBlock<Envelope> _handBack;
    private IMessageInbox? _inbox;

    public BatchMemberInbox(IWolverineRuntime runtime, ILogger logger, CancellationToken cancellation)
    {
        _runtime = runtime;
        _logger = logger;

        TimeSpan[] pauses =
        [
            50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds(), 1.Seconds(), 2.Seconds(), 5.Seconds(),
            10.Seconds(), 30.Seconds()
        ];

        // Same budget as DurableReceiver's mark-as-handled (GH-4664) and its hand-back after a failed dead letter
        // write (GH-5105): the rows are durable and still owned by this node, so there is nothing to lose by waiting
        _markAsHandled = new RetryBlock<Envelope[]>((members, _) => inbox.MarkIncomingEnvelopeAsHandledAsync(members),
            logger, cancellation)
        {
            MaximumAttempts = int.MaxValue,
            Pauses = pauses
        };

        _handBack = new RetryBlock<Envelope>((member, _) => inbox.ScheduleExecutionAsync(member), logger,
            cancellation)
        {
            MaximumAttempts = int.MaxValue,
            Pauses = pauses
        };
    }

    /// <summary>
    /// The pauses between attempts at one member's dead-letter write before the move is given up on. The write is
    /// awaited by the failing batch's continuation, so the budget is kept short, as in DurableReceiver.
    /// </summary>
    internal TimeSpan[] DeadLetterWritePauses { get; set; } =
        [50.Milliseconds(), 250.Milliseconds(), 1.Seconds(), 2.Seconds()];

    /// <summary>How long a member that could not be dead lettered waits before it is executed again</summary>
    internal TimeSpan DeadLetterRedeliveryDelay { get; set; } = 30.Seconds();

    // Resolved on first use: a buffered queue is built long before anyone knows whether it will ever execute a batch,
    // and most never do
    private IMessageInbox inbox => _inbox ??= _runtime.Stores != null && _runtime.Stores.HasAnyAncillaryStores()
        ? new DelegatingMessageInbox(_runtime.Storage.Inbox, _runtime.Stores)
        : _runtime.Storage.Inbox;

    /// <summary>
    /// Does this member have an inbox row that still has to be settled? Only a member that a durable listener or
    /// queue persisted does; one sent locally to a buffered queue, or received on a buffered listener, has none.
    /// </summary>
    public static bool HasUnsettledRow(Envelope member)
    {
        return member.WasPersistedInInbox && member.Status != EnvelopeStatus.Handled;
    }

    /// <summary>
    /// The batch reached its terminal: mark every member that still has an unsettled inbox row as handled
    /// </summary>
    public async ValueTask MarkMembersAsHandledAsync(Envelope batchEnvelope)
    {
        var members = batchEnvelope.Batch;
        if (members == null || members.Length == 0) return;

        List<Envelope>? withRows = null;
        foreach (var member in members)
        {
            member.InBatch = false;
            if (HasUnsettledRow(member))
            {
                (withRows ??= new List<Envelope>(members.Length)).Add(member);
            }
        }

        if (withRows == null) return;

        await _markAsHandled.PostAsync(withRows.ToArray()).ConfigureAwait(false);
    }

    /// <summary>
    /// Write one member to dead letter storage, retrying briefly
    /// </summary>
    /// <returns>Null if the dead letter was written, or the last failure if it could not be</returns>
    public async Task<Exception?> TryMoveToDeadLetterStorageAsync(Envelope member, Exception? exception,
        CancellationToken cancellation)
    {
        var pauses = DeadLetterWritePauses;
        for (var attempt = 0;; attempt++)
        {
            try
            {
                await inbox.MoveToDeadLetterStorageAsync(member, exception).ConfigureAwait(false);
                return null;
            }
            catch (Exception e)
            {
                if (attempt >= pauses.Length || cancellation.IsCancellationRequested) return e;

                _logger.LogWarning(e,
                    "Error moving batch member {EnvelopeId} to dead letter storage on attempt {Attempt}; trying again",
                    member.Id, attempt + 1);

                try
                {
                    await Task.Delay(pauses[attempt], cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return e;
                }
            }
        }
    }

    /// <summary>
    /// B2. A member that could not be dead lettered is not lost and not settled: its inbox row is handed back,
    /// unowned, to be executed again after <see cref="DeadLetterRedeliveryDelay"/>, exactly as DurableReceiver does
    /// for a member of a batch on a durable queue (GH-5105)
    /// </summary>
    public async ValueTask HandBackAsync(Envelope member)
    {
        member.OwnerId = TransportConstants.AnyNode;
        member.ScheduledTime = DateTimeOffset.UtcNow.Add(DeadLetterRedeliveryDelay);
        member.Status = EnvelopeStatus.Scheduled;
        member.InBatch = false;

        await _handBack.PostAsync(member).ConfigureAwait(false);
    }

    public async Task DrainAsync()
    {
        await _markAsHandled.DrainAsync().ConfigureAwait(false);
        await _handBack.DrainAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _markAsHandled.Dispose();
        _handBack.Dispose();
    }
}
