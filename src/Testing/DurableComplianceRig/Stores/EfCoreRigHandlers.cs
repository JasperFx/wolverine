using DurableComplianceRig.Infrastructure;
using Wolverine;

namespace DurableComplianceRig.Stores;

/// <summary>
/// The rig handlers for EF Core. They only add ledger rows to the <see cref="RigDbContext" />: the EF Core
/// transactional middleware opens the transaction, saves, marks the inbox row and commits, exactly as it would for an
/// application handler. <see cref="RigEfInterceptor" /> finishes each execution when that commit lands.
/// </summary>
public static class EfCoreRigHandlers
{
    public static Task Handle(RigMessage message, Envelope envelope, RigDbContext db, RigNodeName node) =>
        recordAsync(db, envelope, [message.Id], node);

    public static Task Handle(RigItem[] items, Envelope envelope, RigDbContext db, RigNodeName node) =>
        recordAsync(db, envelope, items.Select(x => x.Id).ToArray(), node);

    private static async Task recordAsync(RigDbContext db, Envelope envelope, IReadOnlyList<Guid> ids, RigNodeName node)
    {
        db.Envelope = envelope;
        var begun = new List<(Step step, Execution execution)>();
        foreach (var id in ids)
        {
            var (step, execution) = await Script.BeginAsync(id, node.Name, envelope.Message is RigItem[] ? null : envelope.Id);
            begun.Add((step, execution));
            db.Pending.Add(execution);
        }

        foreach (var (_, execution) in begun)
        {
            db.Ledger.Add(new LedgerEntry
            {
                MessageId = execution.MessageId,
                Attempt = execution.Attempt,
                Node = execution.Node,
                Tenant = envelope.TenantId,
                StartedAt = execution.StartedAt,
                RecordedAt = DateTimeOffset.UtcNow
            });

            if (EfScript.FaultFor(execution.MessageId, execution.Attempt) == EfFault.BreakSaveChanges)
            {
                // node is varchar(100): the server refuses this row, and the whole SaveChanges with it
                db.Ledger.Add(new LedgerEntry
                {
                    MessageId = execution.MessageId,
                    Attempt = execution.Attempt,
                    Node = new string('x', 200),
                    StartedAt = execution.StartedAt,
                    RecordedAt = DateTimeOffset.UtcNow
                });
            }
        }

        try
        {
            foreach (var (step, execution) in begun) Script.BeforeCommit(step, execution);
        }
        catch (Exception e)
        {
            db.FinishPending($"failed: {e.GetType().Name}");
            throw;
        }
    }
}
