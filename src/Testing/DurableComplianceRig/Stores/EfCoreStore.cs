using System.Collections.Concurrent;
using System.Data.Common;
using DurableComplianceRig.Infrastructure;
using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.SqlServer;

namespace DurableComplianceRig.Stores;

/// <summary>
/// EF Core on SQL Server: Wolverine's SQL Server message store, a Wolverine-mapped <see cref="RigDbContext" /> in the
/// same database and schema, and the EF Core transactional middleware applied to every rig handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="EffectSemantics.ExactlyOnce" />.</b> On a durable endpoint the envelope arrives with
/// <c>WasPersistedInInbox</c>, so <c>EfCoreEnvelopeTransaction.CommitAsync</c> runs
/// <c>MarkIncomingEnvelopeAsHandledInTransactionAsync</c> on the DbContext's own connection and transaction, then
/// sets <c>Status = Handled</c> so <c>DurableReceiver</c> skips its own (later, separate) mark. Read in the code, and
/// observed: <see cref="RigEfInterceptor" /> reads the inbox row on the committing transaction just before the commit
/// (<see cref="EfScript.InboxStatusAtCommit" />), and the
/// <c>the_inbox_row_is_marked_handled_inside_the_ef_core_transaction</c> spec requires it to say Handled.
/// </para>
/// <para>
/// The ledger table is created by <see cref="RigDatabase.EnsureLedgerAsync" />; <see cref="LedgerEntry" /> only maps
/// onto it (excluded from migrations), so the shared reader and checker see EF's rows exactly as they see ADO.NET's.
/// </para>
/// </remarks>
public class EfCoreStore(IdempotencyStyle? idempotency = null) : StoreProfile
{
    public static readonly string Schema = "rig_ef" + RigEnvironment.SchemaSuffix;

    private readonly RigDatabase _database = new("efcore", Dialect.SqlServer,
        () => new SqlConnection(Servers.SqlServerConnectionString), Schema);

    public override string Name => idempotency == null ? "EF Core on SQL Server" : $"EF Core on SQL Server ({idempotency} idempotency)";
    public override string Key => "ef";
    public override EffectSemantics Semantics => EffectSemantics.ExactlyOnce;
    public override IReadOnlyList<RigDatabase> Databases => [_database];

    public override void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues)
    {
        SqlServerQueues.PersistMessages(opts, Schema, withDatabaseQueues);

        opts.Services.AddDbContextWithWolverineIntegration<RigDbContext>(
            x => x.UseSqlServer(Servers.SqlServerConnectionString).AddInterceptors(RigEfInterceptor.Instance), Schema);

        opts.UseEntityFrameworkCoreTransactions();
        if (idempotency.HasValue)
        {
            opts.Policies.AutoApplyTransactions(idempotency.Value);
        }
        else
        {
            opts.Policies.AutoApplyTransactions();
        }

        opts.Discovery.IncludeType(typeof(EfCoreRigHandlers));
    }

    public override void ConfigureDatabaseQueue(WolverineOptions opts, string queueName) =>
        SqlServerQueues.Configure(opts, queueName);
}

/// <summary>One ledger row, mapped onto the rig's own <c>rig_ledger</c> table.</summary>
public class LedgerEntry
{
    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public int Attempt { get; set; }
    public string Node { get; set; } = "";
    public string? Tenant { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public class RigDbContext(DbContextOptions<RigDbContext> options) : DbContext(options)
{
    /// <summary>The executions whose ledger rows this context will commit, finished by the interceptor.</summary>
    public List<Execution> Pending { get; } = [];

    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LedgerEntry>(map =>
        {
            // The table belongs to RigDatabase, not to this model
            map.ToTable(RigDatabase.LedgerTable, EfCoreStore.Schema, x => x.ExcludeFromMigrations());
            map.HasKey(x => x.Id);
            map.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            map.Property(x => x.MessageId).HasColumnName("message_id");
            map.Property(x => x.Attempt).HasColumnName("attempt");

            // Deliberately no max length: EF would size the parameter, and an oversized value must reach the
            // server and fail there (EfFault.BreakSaveChanges)
            map.Property(x => x.Node).HasColumnName("node").IsUnicode(false);
            map.Property(x => x.Tenant).HasColumnName("tenant").IsUnicode(false);
            map.Property(x => x.StartedAt).HasColumnName("started_at");
            map.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        });
    }

    public void FinishPending(string outcome)
    {
        foreach (var execution in Pending.Where(x => x.FinishedAt == null)) execution.Finish(outcome);
    }

    // Lightweight mode, or a failure before any transaction began, never reaches the interceptor
    public override void Dispose()
    {
        FinishPending("disposed without a commit");
        base.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        FinishPending("disposed without a commit");
        return base.DisposeAsync();
    }
}

/// <summary>Faults the EF Core handlers can be scripted with, beyond <see cref="Step" />.</summary>
public enum EfFault
{
    None,

    /// <summary>Add a ledger row the server rejects, so the middleware's <c>SaveChangesAsync</c> throws.</summary>
    BreakSaveChanges,

    /// <summary>Throw <see cref="RigCommitFailure" /> as the transaction commits, after Wolverine's inbox bookkeeping.</summary>
    FailCommitDiscarded,

    /// <summary>Throw <see cref="RigFailure" /> as the transaction commits, so the retry/dead letter policy applies.</summary>
    FailCommit
}

/// <summary>A commit failure the EF Core scenarios discard.</summary>
public class RigCommitFailure(string message) : Exception(message);

/// <summary>Per message and attempt EF faults, and what the interceptor observed. Static for the same reason as <see cref="Script" />.</summary>
public static class EfScript
{
    private static readonly ConcurrentDictionary<Guid, EfFault[]> _faults = new();

    /// <summary>The message's inbox status read on the committing transaction, per message id, most recent last.</summary>
    public static readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> InboxStatusAtCommit = new();

    /// <summary>Off by default: one extra query per commit.</summary>
    public static volatile bool ObserveInbox;

    public static void Reset()
    {
        _faults.Clear();
        InboxStatusAtCommit.Clear();
        ObserveInbox = false;
    }

    public static void Set(Guid id, params EfFault[] faults) => _faults[id] = faults;

    public static EfFault FaultFor(Guid id, int attempt) =>
        _faults.TryGetValue(id, out var faults) ? faults[Math.Min(attempt, faults.Length) - 1] : EfFault.None;
}

/// <summary>
/// Finishes the rig's executions when their transaction commits or rolls back -- the middleware commits after the
/// handler returns, so the handler cannot -- and injects the scripted commit failures.
/// </summary>
public class RigEfInterceptor : DbTransactionInterceptor
{
    public static readonly RigEfInterceptor Instance = new();

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not RigDbContext context) return result;

        if (EfScript.ObserveInbox)
        {
            foreach (var execution in context.Pending.Where(x => x.EnvelopeId.HasValue))
            {
                var status = await readInboxStatusAsync(transaction, execution.EnvelopeId!.Value, cancellationToken);
                EfScript.InboxStatusAtCommit.GetOrAdd(execution.MessageId, _ => new ConcurrentQueue<string>())
                    .Enqueue(status ?? "no row");
            }
        }

        foreach (var execution in context.Pending)
        {
            switch (EfScript.FaultFor(execution.MessageId, execution.Attempt))
            {
                case EfFault.FailCommitDiscarded:
                    context.FinishPending("commit failed (discard)");
                    throw new RigCommitFailure($"Scripted commit failure of {execution.MessageId} on attempt {execution.Attempt}");

                case EfFault.FailCommit:
                    context.FinishPending("commit failed");
                    throw new RigFailure($"Scripted commit failure of {execution.MessageId} on attempt {execution.Attempt}");
            }
        }

        return result;
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        (eventData.Context as RigDbContext)?.FinishPending("committed");
        return Task.CompletedTask;
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        (eventData.Context as RigDbContext)?.FinishPending("rolled back");
        return Task.CompletedTask;
    }

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        (eventData.Context as RigDbContext)?.FinishPending($"transaction failed: {eventData.Exception.GetType().Name}");
        return Task.CompletedTask;
    }

    private static async Task<string?> readInboxStatusAsync(DbTransaction transaction, Guid envelopeId,
        CancellationToken cancellation)
    {
        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"select status from {EfCoreStore.Schema}.wolverine_incoming_envelopes where id = @id";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = envelopeId;
        cmd.Parameters.Add(parameter);
        return await cmd.ExecuteScalarAsync(cancellation) as string;
    }
}
