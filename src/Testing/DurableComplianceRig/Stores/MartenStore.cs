using DurableComplianceRig.Infrastructure;
using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Postgresql;

namespace DurableComplianceRig.Stores;

/// <summary>
/// Marten as the message store, with <c>AutoApplyTransactions</c>: the rig handler takes an
/// <see cref="IDocumentSession" />, queues its ledger row into the session's unit of work, and Wolverine's Marten
/// middleware commits it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why ExactlyOnce.</b> On a durable endpoint <c>FlushOutgoingMessagesOnCommit.BeforeSaveChangesAsync</c>
/// (src/Persistence/Wolverine.Marten/FlushOutgoingMessagesOnCommit.cs) queues the store's
/// <c>BuildMarkIncomingAsHandled</c> statements into the same Marten batch as the handler's work, so the row
/// turns Handled in the handler's own transaction. It does so only when <c>Envelope.WasPersistedInInbox</c> is
/// true, which every durable path sets before the handler runs: <c>DurableLocalQueue.storeAndEnqueueAsync</c>
/// after its insert, and <c>DurableReceiver.EnqueueAsync</c> for every envelope it queues -- including a
/// PostgreSQL queue's, whose row the durable pop (<c>PostgresqlQueueListener.TryPopDurablyAsync</c>) wrote. The
/// database queue case was checked at runtime, not just read: the flag is already true when the chain starts.
/// </para>
/// </remarks>
public class MartenStore(IdempotencyStyle? idempotency = null) : StoreProfile
{
    public static readonly string Schema = "rig_marten" + RigEnvironment.SchemaSuffix;

    private readonly RigDatabase _database = new("marten", Dialect.Postgresql,
        () => new NpgsqlConnection(Servers.PostgresConnectionString), Schema);

    public override string Name => idempotency.HasValue ? $"Marten ({idempotency} idempotency)" : "Marten";
    public override string Key => "marten";
    public override EffectSemantics Semantics => EffectSemantics.ExactlyOnce;
    public override IReadOnlyList<RigDatabase> Databases => [_database];

    public RigDatabase Database => _database;

    public override void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues)
    {
        opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = Schema;
                m.DisableNpgsqlLogging = true;
            })
            .IntegrateWithWolverine(x =>
            {
                // The message tables land here explicitly rather than by falling back to DatabaseSchemaName, so
                // RigDatabase reads the right schema whatever the Durability options say
                x.MessageStorageSchemaName = Schema;

                // Otherwise the queue tables go to the shared "wolverine_queues" schema, and parallel runs collide
                x.TransportSchemaName = Schema;
            });

        // Calling AutoApplyTransactions twice registers the policy twice, so the idempotency style is the
        // profile's to choose rather than a scenario's
        if (idempotency.HasValue) opts.Policies.AutoApplyTransactions(idempotency.Value);
        else opts.Policies.AutoApplyTransactions();

        opts.Services.AddSingleton(new MartenLedger(_database));
        opts.Discovery.IncludeType(typeof(MartenRigHandler));
    }

    public override void ConfigureDatabaseQueue(WolverineOptions opts, string queueName)
    {
        // The Marten integration creates the PostgreSQL transport itself (MartenIntegration.Configure), so there is
        // no EnableMessageTransport call to make: listening to a queue is enough
        opts.PublishMessage<RigMessage>().ToPostgresqlQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.PublishMessage<RigItem>().ToPostgresqlQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.ListenToPostgresqlQueue(queueName);
    }
}
