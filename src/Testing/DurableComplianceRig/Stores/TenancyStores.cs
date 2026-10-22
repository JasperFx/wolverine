using System.Data.Common;
using DurableComplianceRig.Infrastructure;
using JasperFx;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer;

namespace DurableComplianceRig.Stores;

/// <summary>Wolverine's own PostgreSQL message store with static tenants, one database each.</summary>
public class PostgresqlTenancyStore() : TenancyStoreProfile(new PostgresqlTenancyDatabases("tpg"))
{
    public override string Name => "PostgreSQL, a database per tenant";
    public override string Key => "tpg";
    public override string Schema => "rig";
    public override EffectSemantics Semantics => EffectSemantics.AtLeastOnce;

    public override void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues)
    {
        Tenancy.EnsureCreated();
        opts.PersistMessagesWithPostgresql(Tenancy.ConnectionStringFor(TenancyDatabases.MainLabel), Schema)
            .RegisterStaticTenants(tenants =>
            {
                foreach (var tenant in Tenants) tenants.Register(tenant, Tenancy.ConnectionStringFor(tenant));
            });

        opts.Services.AddSingleton(new TenantRdbmsLedger(this));
        opts.Discovery.IncludeType(typeof(TenantRdbmsRigHandler));
    }
}

/// <summary>Wolverine's own SQL Server message store with static tenants, one database each.</summary>
public class SqlServerTenancyStore() : TenancyStoreProfile(new SqlServerTenancyDatabases("tsql"))
{
    public override string Name => "SQL Server, a database per tenant";
    public override string Key => "tsql";
    public override string Schema => "rig";
    public override EffectSemantics Semantics => EffectSemantics.AtLeastOnce;

    public override void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues)
    {
        Tenancy.EnsureCreated();
        opts.PersistMessagesWithSqlServer(Tenancy.ConnectionStringFor(TenancyDatabases.MainLabel), Schema)
            .RegisterStaticTenants(tenants =>
            {
                foreach (var tenant in Tenants) tenants.Register(tenant, Tenancy.ConnectionStringFor(tenant));
            });

        opts.Services.AddSingleton(new TenantRdbmsLedger(this));
        opts.Discovery.IncludeType(typeof(TenantRdbmsRigHandler));
    }
}

/// <summary>
/// Marten with a database per tenant and <c>IntegrateWithWolverine</c>: each tenant database holds its Marten
/// documents, its Wolverine envelope tables and its ledger. The handler is the ordinary <see cref="MartenRigHandler" />;
/// its session is opened on the envelope's tenant, so the ledger row and the in-transaction inbox mark both go to
/// the tenant database. Every database uses the same schema, so one <see cref="MartenLedger" /> serves them all.
/// </summary>
public class MartenTenancyStore() : TenancyStoreProfile(new PostgresqlTenancyDatabases("tmarten"))
{
    public override string Name => "Marten, a database per tenant";
    public override string Key => "tmarten";
    public override string Schema => "rig";
    public override EffectSemantics Semantics => EffectSemantics.ExactlyOnce;

    public override void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues)
    {
        Tenancy.EnsureCreated();
        opts.Services.AddMarten(m =>
            {
                m.DatabaseSchemaName = Schema;
                m.DisableNpgsqlLogging = true;
                m.MultiTenantedDatabases(tenancy =>
                {
                    foreach (var tenant in Tenants)
                    {
                        tenancy.AddSingleTenantDatabase(Tenancy.ConnectionStringFor(tenant), tenant);
                    }
                });
            })
            .IntegrateWithWolverine(x =>
            {
                x.MainDatabaseConnectionString = Tenancy.ConnectionStringFor(TenancyDatabases.MainLabel);
                x.MessageStorageSchemaName = Schema;
                x.TransportSchemaName = Schema;
            });

        opts.Policies.AutoApplyTransactions();
        opts.Services.AddSingleton(new MartenLedger(DatabaseFor(null)));
        opts.Discovery.IncludeType(typeof(MartenRigHandler));
    }
}

/// <summary>
/// EF Core managed multi-tenancy on SQL Server: static tenants on Wolverine's SQL Server store, and the rig's
/// <see cref="RigDbContext" /> built per tenant by <c>AddDbContextWithWolverineManagedMultiTenancy</c>. The schema is
/// <see cref="EfCoreStore.Schema" /> in every database because <see cref="RigDbContext" /> maps the ledger there;
/// the databases are this profile's own, so nothing is shared with the plain EF Core profile.
/// </summary>
public class EfCoreTenancyStore() : TenancyStoreProfile(new SqlServerTenancyDatabases("tef"))
{
    public override string Name => "EF Core on SQL Server, a database per tenant";
    public override string Key => "tef";
    public override string Schema => EfCoreStore.Schema;
    public override EffectSemantics Semantics => EffectSemantics.ExactlyOnce;

    public override void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues)
    {
        Tenancy.EnsureCreated();
        opts.PersistMessagesWithSqlServer(Tenancy.ConnectionStringFor(TenancyDatabases.MainLabel), Schema)
            .RegisterStaticTenants(tenants =>
            {
                foreach (var tenant in Tenants) tenants.Register(tenant, Tenancy.ConnectionStringFor(tenant));
            });

        opts.Services.AddDbContextWithWolverineManagedMultiTenancy<RigDbContext>((builder, connectionString, _) =>
        {
            builder.UseSqlServer(connectionString.Value)
                .AddInterceptors(RigEfInterceptor.Instance, TenancyHoldInterceptor.Instance);
        }, AutoCreate.None);

        opts.UseEntityFrameworkCoreTransactions();
        opts.Policies.AutoApplyTransactions();
        opts.Discovery.IncludeType(typeof(EfCoreRigHandlers));
    }
}

/// <summary>
/// <see cref="EfCoreStore" /> plus <see cref="TenancyHoldInterceptor" />: the one-database control for the per-tenant
/// crash scenario. Same schema and database as <see cref="EfCoreStore" />, so never run the two at once.
/// </summary>
public class EfCoreHoldStore : StoreProfile
{
    private readonly RigDatabase _database = new EfCoreStore().Databases[0];

    public override string Name => "EF Core on SQL Server (with a post-commit hold)";
    public override string Key => "ef";
    public override EffectSemantics Semantics => EffectSemantics.ExactlyOnce;
    public override IReadOnlyList<RigDatabase> Databases => [_database];

    public override void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues)
    {
        SqlServerQueues.PersistMessages(opts, EfCoreStore.Schema, withDatabaseQueues);
        opts.Services.AddDbContextWithWolverineIntegration<RigDbContext>(
            x => x.UseSqlServer(IntegrationTests.Servers.SqlServerConnectionString)
                .AddInterceptors(RigEfInterceptor.Instance, TenancyHoldInterceptor.Instance), EfCoreStore.Schema);
        opts.UseEntityFrameworkCoreTransactions();
        opts.Policies.AutoApplyTransactions();
        opts.Discovery.IncludeType(typeof(EfCoreRigHandlers));
    }

    public override void ConfigureDatabaseQueue(WolverineOptions opts, string queueName) =>
        SqlServerQueues.Configure(opts, queueName);
}

/// <summary>
/// Holds an EF Core handler after its commit and before the durable receiver settles the row, through the same
/// <see cref="MartenRigHooks" /> holds the Marten ledger uses -- the window a crash has to hit to tell a row marked
/// inside the transaction apart from one marked after it.
/// </summary>
public class TenancyHoldInterceptor : DbTransactionInterceptor
{
    public static readonly TenancyHoldInterceptor Instance = new();

    /// <summary>
    /// The decisive experiment for the per-tenant EF Core crash (<c>RIG_EF_TENANT_MARK_IN_TX=1</c>): mark the inbox
    /// row Handled on the committing tenant transaction, and set the envelope Handled so the receiver skips its own
    /// mark -- exactly what <c>EfCoreEnvelopeTransaction.CommitAsync</c> does on the one-database path, and what the
    /// multi-tenant commit frame (<c>CommitTenantedDbContextTransaction</c>) never calls. If the duplicate goes away,
    /// the missing in-transaction mark is the whole mechanism.
    /// </summary>
    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Environment.GetEnvironmentVariable("RIG_EF_TENANT_MARK_IN_TX") != "1") return result;
        if (eventData.Context is not RigDbContext { Envelope: { WasPersistedInInbox: true } envelope }) return result;

        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            $"update {EfCoreStore.Schema}.wolverine_incoming_envelopes set status = 'Handled', keep_until = @keep where id = @id";
        var id = cmd.CreateParameter();
        id.ParameterName = "@id";
        id.Value = envelope.Id;
        cmd.Parameters.Add(id);
        var keep = cmd.CreateParameter();
        keep.ParameterName = "@keep";
        keep.Value = DateTimeOffset.UtcNow.AddMinutes(5);
        cmd.Parameters.Add(keep);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        envelope.Status = EnvelopeStatus.Handled;
        return result;
    }

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not RigDbContext context) return;
        // The first attempt only: the hold stays registered until it is released, so a recovered run on another
        // node would otherwise park behind the same hold and never reach the receiver's own mark
        foreach (var execution in context.Pending.Where(x => x.Attempt == 1))
        {
            await MartenRigHooks.HoldAfterCommitAsync(execution.MessageId);
        }
    }
}

/// <summary>
/// <see cref="RdbmsLedger" /> for a database per tenant: the effect goes to the envelope's tenant database, and the
/// outbox is enlisted on that tenant's own message database, which is what an application handler on a
/// multi-tenanted plain RDBMS store has to do by hand.
/// </summary>
public class TenantRdbmsLedger(TenancyStoreProfile profile)
{
    /// <summary>Every failure the script did not ask for, with its stack, for the test output.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> UnscriptedFailures = new();

    public async Task RecordAsync(IMessageContext context, Envelope envelope, Guid id, RigNodeName node)
    {
        var (step, execution) = await Script.BeginAsync(id, node.Name, envelope.Id);
        var database = profile.DatabaseFor(envelope.TenantId);

        await using var conn = database.Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        WatchedEnvelopeTransaction? enlisted = null;
        if (context is MessageBus bus)
        {
            var store = bus.Storage is MultiTenantedMessageStore tenanted
                ? await tenanted.GetDatabaseAsync(envelope.TenantId)
                : bus.Storage;

            if (store is IMessageDatabase messageDatabase)
            {
                enlisted = new WatchedEnvelopeTransaction(new DatabaseEnvelopeTransaction(messageDatabase, tx), id);
                await bus.EnlistInOutboxAsync(enlisted);
            }
        }

        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = database.InsertLedgerSql;
                add(cmd, "message_id", id);
                add(cmd, "attempt", execution.Attempt);
                add(cmd, "node", execution.Node);
                add(cmd, "tenant", (object?)envelope.TenantId ?? DBNull.Value);
                add(cmd, "started_at", execution.StartedAt);
                add(cmd, "recorded_at", DateTimeOffset.UtcNow);
                await cmd.ExecuteNonQueryAsync();
            }

            Script.BeforeCommit(step, execution);

            await tx.CommitAsync();
            if (context is MessageContext messageContext) await messageContext.FlushOutgoingMessagesAsync();
            execution.Finish("committed");
        }
        catch (Exception e)
        {
            if (execution.FinishedAt == null) execution.Finish($"failed: {e.GetType().Name}");
            if (e is not RigFailure) UnscriptedFailures.Enqueue($"{id} attempt {execution.Attempt}: {e}");
            throw;
        }
        finally
        {
            enlisted?.HandlerIsDone();
        }
    }

    private static void add(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }
}

/// <summary>The rig handler for the plain RDBMS stores with a database per tenant.</summary>
public static class TenantRdbmsRigHandler
{
    public static Task Handle(RigMessage message, Envelope envelope, IMessageContext context, RigNodeName node,
        TenantRdbmsLedger ledger) => ledger.RecordAsync(context, envelope, message.Id, node);
}
