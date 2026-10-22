using System.Data.Common;
using DurableComplianceRig.Infrastructure;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace DurableComplianceRig.Stores;

/// <summary>
/// The plain RDBMS stores have no transactional middleware for handlers, so a rig handler does what an
/// application would: open a transaction, write its effect, enlist the outbox in it, commit, then flush. The
/// inbox row is marked handled AFTER that commit, by the durable receiver -- which is what makes these stores
/// <see cref="EffectSemantics.AtLeastOnce" />.
/// </summary>
public class RdbmsLedger(RigDatabase database)
{
    public async Task RecordAsync(IMessageContext context, Envelope envelope, IReadOnlyList<Guid> ids, RigNodeName node)
    {
        var begun = new List<(Guid id, Step step, Execution execution)>();
        try
        {
            foreach (var id in ids)
            {
                var (step, execution) = await Script.BeginAsync(id, node.Name, envelope.Message is RigItem[] ? null : envelope.Id);
                begun.Add((id, step, execution));
            }
        }
        catch (Exception e)
        {
            // One batch member's scripted failure fails the whole batch. The members begun before it have to be
            // finished too, or the checker sees them as still running and reports every retry as overlapping them
            foreach (var (_, _, execution) in begun) execution.Finish($"abandoned: {e.GetType().Name}");
            throw;
        }

        await using var conn = database.Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        WatchedEnvelopeTransaction? enlisted = null;
        if (context is MessageBus bus && bus.Storage is IMessageDatabase messageDatabase)
        {
            var transaction = new DatabaseEnvelopeTransaction(messageDatabase, tx);
            if (Environment.GetEnvironmentVariable("RIG_NO_WATCH") == "1")
            {
                await bus.EnlistInOutboxAsync(transaction);
            }
            else
            {
                enlisted = new WatchedEnvelopeTransaction(transaction, ids[0]);
                await bus.EnlistInOutboxAsync(enlisted);
            }
        }

        try
        {
            foreach (var (id, _, execution) in begun)
            {
                await insertAsync(conn, tx, id, execution, envelope.TenantId);
            }

            foreach (var (_, step, execution) in begun) Script.BeforeCommit(step, execution);

            await tx.CommitAsync();
            if (context is MessageContext messageContext) await messageContext.FlushOutgoingMessagesAsync();

            foreach (var (_, _, execution) in begun) execution.Finish("committed");
        }
        catch (Exception e)
        {
            foreach (var (_, _, execution) in begun.Where(x => x.execution.FinishedAt == null))
            {
                execution.Finish($"failed: {e.GetType().Name}");
            }

            throw;
        }
        finally
        {
            enlisted?.HandlerIsDone();
        }
    }

    private async Task insertAsync(DbConnection conn, DbTransaction tx, Guid id, Execution execution, string? tenant)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = database.InsertLedgerSql;
        add(cmd, "message_id", id);
        add(cmd, "attempt", execution.Attempt);
        add(cmd, "node", execution.Node);
        add(cmd, "tenant", (object?)tenant ?? DBNull.Value);
        add(cmd, "started_at", execution.StartedAt);
        add(cmd, "recorded_at", DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void add(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }
}

/// <summary>The rig handlers for the plain RDBMS stores.</summary>
public static class RdbmsRigHandler
{
    public static Task Handle(RigMessage message, Envelope envelope, IMessageContext context, RigNodeName node,
        RdbmsLedger ledger) => ledger.RecordAsync(context, envelope, [message.Id], node);

    public static Task Handle(RigItem[] items, Envelope envelope, IMessageContext context, RigNodeName node,
        RdbmsLedger ledger)
    {
        BatchItemScript.ThrowIfAnyIsPoison(items);
        return ledger.RecordAsync(context, envelope, items.Select(x => x.Id).ToArray(), node);
    }
}
