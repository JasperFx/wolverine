using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Marten.Publishing;
using Wolverine.MySql;
using Wolverine.Oracle;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.RDBMS;
using Wolverine.Sqlite;
using Wolverine.Runtime;
using Wolverine.SqlServer;

namespace DurabilityChaosRig;

public record Work(Guid Id, int Flags);

public record LocalStep(Guid Id);

public record DelayedStep(Guid Id);

public class FlakyException() : Exception("First attempt fails on purpose");

public class PoisonException() : Exception("Fails until the driver ships the fix, then succeeds on replay");

/// <summary>
/// What the producer committed, in the same transaction as the outbox rows for it
/// </summary>
public class Sent
{
    public Guid Id { get; set; }
    public int Flags { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>
/// One row per time a handler committed. Written in the handler's own transaction, which also marks the inbox row
/// handled, so two rows for one (message, stage) is a committed duplicate
/// </summary>
public class Processed
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public string Stage { get; set; } = "";
    public string Node { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>The Wolverine envelope id of the execution that committed, so a duplicate can be followed through the worker logs (GH-5426)</summary>
    public string EnvelopeId { get; set; } = "";
}

public static class Flags
{
    public const int FailFirstAttempt = 1;
    public const int Delayed = 2;
    public const int Local = 4;
    public const int Poison = 8;
}

/// <summary>
/// The handlers' shared logic; the store-specific handler classes below only differ in how they write the ledger
/// </summary>
public static class Steps
{
    public static async Task<OutgoingMessages> WorkAsync(Work work, Envelope envelope, Action<Processed> store)
    {
        // Widen the in-flight window a kill can land in. CHAOS_HANDLER_DELAY=min-max in milliseconds
        await Task.Delay(Node.HandlerDelay());

        if ((work.Flags & Flags.FailFirstAttempt) != 0 && envelope.Attempts <= 1) throw new FlakyException();
        if ((work.Flags & Flags.Poison) != 0 && !File.Exists(Node.PoisonFixedFile)) throw new PoisonException();

        store(Ledger.Record(work.Id, "work", envelope));

        var outgoing = new OutgoingMessages();
        if ((work.Flags & Flags.Local) != 0) outgoing.Add(new LocalStep(work.Id));
        if ((work.Flags & Flags.Delayed) != 0) outgoing.Add(new DelayedStep(work.Id).DelayedFor(2.Seconds()));
        return outgoing;
    }
}

public static class MartenWorkHandler
{
    public static Task<OutgoingMessages> Handle(Work work, IDocumentSession session, Envelope envelope) =>
        Steps.WorkAsync(work, envelope, x => session.Store(x));

    public static async Task Handle(LocalStep step, IDocumentSession session, Envelope envelope)
    {
        await Task.Delay(Random.Shared.Next(0, 20));
        session.Store(Ledger.Record(step.Id, "local", envelope));
    }

    public static void Handle(DelayedStep step, IDocumentSession session, Envelope envelope) =>
        session.Store(Ledger.Record(step.Id, "delayed", envelope));
}

public static class EfWorkHandler
{
    public static Task<OutgoingMessages> Handle(Work work, ChaosDbContext db, Envelope envelope) =>
        Steps.WorkAsync(work, envelope, x => db.Processed.Add(x));

    public static async Task Handle(LocalStep step, ChaosDbContext db, Envelope envelope)
    {
        await Task.Delay(Random.Shared.Next(0, 20));
        db.Processed.Add(Ledger.Record(step.Id, "local", envelope));
    }

    public static void Handle(DelayedStep step, ChaosDbContext db, Envelope envelope) =>
        db.Processed.Add(Ledger.Record(step.Id, "delayed", envelope));
}

/// <summary>
/// MySQL, Oracle and SQLite have no transactional middleware for a plain connection, so these handlers do what the
/// EF Core and Marten integrations do by hand: one ADO.NET transaction holds the ledger row, the cascaded messages
/// (through an enlisted outbox) and the mark that retires this envelope's inbox row, and the cascades are only
/// handed to their queues once it commits
/// </summary>
public static class AdoWorkHandler
{
    public static async Task Handle(Work work, IMessageContext context, IWolverineRuntime runtime, Envelope envelope)
    {
        await Task.Delay(Node.HandlerDelay());

        if ((work.Flags & Flags.FailFirstAttempt) != 0 && envelope.Attempts <= 1) throw new FlakyException();
        if ((work.Flags & Flags.Poison) != 0 && !File.Exists(Node.PoisonFixedFile)) throw new PoisonException();

        await commitAsync(runtime, (MessageContext)context, envelope, Ledger.Record(work.Id, "work", envelope),
            async () =>
            {
                if ((work.Flags & Flags.Local) != 0) await context.PublishAsync(new LocalStep(work.Id));
                if ((work.Flags & Flags.Delayed) != 0)
                {
                    await context.PublishAsync(new DelayedStep(work.Id),
                        new DeliveryOptions { ScheduleDelay = 2.Seconds() });
                }
            });
    }

    public static async Task Handle(LocalStep step, IMessageContext context, IWolverineRuntime runtime, Envelope envelope)
    {
        await Task.Delay(Random.Shared.Next(0, 20));
        await commitAsync(runtime, (MessageContext)context, envelope, Ledger.Record(step.Id, "local", envelope), null);
    }

    public static Task Handle(DelayedStep step, IMessageContext context, IWolverineRuntime runtime, Envelope envelope)
    {
        return commitAsync(runtime, (MessageContext)context, envelope, Ledger.Record(step.Id, "delayed", envelope), null);
    }

    private static async Task commitAsync(IWolverineRuntime runtime, MessageContext context, Envelope envelope,
        Processed processed, Func<Task>? cascade)
    {
        var database = (IMessageDatabase)runtime.Storage;

        await using var conn = Db.Open(Db.NodeConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(database, tx));

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                $"insert into {Db.ProcessedTable} (id, message_id, stage, node, attempts, at, envelope_id) values ({p(0)}, {p(1)}, {p(2)}, {p(3)}, {p(4)}, {p(5)}, {p(6)})";
            add(cmd, 0, Db.Id(processed.Id));
            add(cmd, 1, Db.Id(processed.MessageId));
            add(cmd, 2, processed.Stage);
            add(cmd, 3, processed.Node);
            add(cmd, 4, processed.Attempts);
            add(cmd, 5, Db.Timestamp(processed.At));
            add(cmd, 6, processed.EnvelopeId);
            await cmd.ExecuteNonQueryAsync();
        }

        if (cascade != null) await cascade();

        var marked = false;
        if (envelope.WasPersistedInInbox && envelope.Destination != null)
        {
            await database.MarkIncomingEnvelopeAsHandledInTransactionAsync(conn, tx, envelope,
                DateTimeOffset.UtcNow.Add(runtime.Options.Durability.KeepAfterMessageHandling), CancellationToken.None);
            marked = true;
        }

        await tx.CommitAsync();

        if (marked) envelope.Status = EnvelopeStatus.Handled;

        await context.FlushOutgoingMessagesAsync();
    }

    internal static string p(int i) => Db.Kind == DbKind.Oracle ? $":p{i}" : $"@p{i}";

    internal static void add(System.Data.Common.DbCommand cmd, int i, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = Db.Kind == DbKind.Oracle ? $"p{i}" : $"@p{i}";
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }
}

public class ChaosDbContext(DbContextOptions<ChaosDbContext> options) : DbContext(options)
{
    public DbSet<Sent> Sent => Set<Sent>();
    public DbSet<Processed> Processed => Set<Processed>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sent>(x =>
        {
            x.ToTable("sent", Node.Schema);
            x.HasKey(e => e.Id);
            x.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            x.Property(e => e.Flags).HasColumnName("flags");
            x.Property(e => e.At).HasColumnName("at");
        });

        modelBuilder.Entity<Processed>(x =>
        {
            x.ToTable("processed", Node.Schema);
            x.HasKey(e => e.Id);
            x.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            x.Property(e => e.MessageId).HasColumnName("message_id");
            x.Property(e => e.Stage).HasColumnName("stage").HasMaxLength(20);
            x.Property(e => e.Node).HasColumnName("node").HasMaxLength(20);
            x.Property(e => e.Attempts).HasColumnName("attempts");
            x.Property(e => e.At).HasColumnName("at");
            x.Property(e => e.EnvelopeId).HasColumnName("envelope_id").HasMaxLength(36);
        });
    }
}

public static class Node
{
    public static string Schema => Environment.GetEnvironmentVariable("CHAOS_SCHEMA") ?? "chaos";

    /// <summary>pgqueue, rabbit or sqlqueue</summary>
    public static string Transport => Environment.GetEnvironmentVariable("CHAOS_TRANSPORT") ?? "pgqueue";

    /// <summary>
    /// marten (PostgreSQL), ef (EF Core over SQL Server), efpg (EF Core over PostgreSQL), or mysql, oracle and sqlite
    /// (plain ADO.NET handlers, see <see cref="AdoWorkHandler"/>)
    /// </summary>
    public static string Store => Environment.GetEnvironmentVariable("CHAOS_STORE") ?? "marten";

    private static (int min, int max)? _handlerDelay;

    /// <summary>CHAOS_HANDLER_DELAY=min-max, in milliseconds. 0-40 by default</summary>
    public static TimeSpan HandlerDelay()
    {
        _handlerDelay ??= Environment.GetEnvironmentVariable("CHAOS_HANDLER_DELAY") is { Length: > 0 } spec &&
                          spec.Split('-') is [var min, var max]
            ? (int.Parse(min), int.Parse(max))
            : (0, 40);

        return TimeSpan.FromMilliseconds(Random.Shared.Next(_handlerDelay.Value.min, _handlerDelay.Value.max + 1));
    }

    /// <summary>CHAOS_RETRY_COOLDOWN, in seconds: how long a FlakyException waits before its scheduled retry. 1 by default</summary>
    public static TimeSpan RetryCooldown =>
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("CHAOS_RETRY_COOLDOWN"), out var s) ? s : 1);

    public static string Name => Environment.GetEnvironmentVariable("CHAOS_NODE") ?? "node";
    public static string Dir => Environment.GetEnvironmentVariable("CHAOS_DIR") ?? ".";
    public static string PoisonFixedFile => Path.Combine(Dir, "poison-fixed");

    /// <summary>
    /// CHAOS_SHORT_TIMEOUTS=1 gives every database command five seconds, so a database outage longer than that
    /// times out commits the server applies once it is back -- "the error may mean it succeeded"
    /// </summary>
    private static string shortTimeouts(string connectionString, string separator = ";") =>
        Environment.GetEnvironmentVariable("CHAOS_SHORT_TIMEOUTS") == "1"
            ? connectionString.TrimEnd(';') + separator + "Command Timeout=5"
            : connectionString;

    public static string PostgresConnectionString => shortTimeouts(Servers.PostgresConnectionString);
    public static string SqlServerConnectionString => shortTimeouts(Servers.SqlServerConnectionString);

    public static IHostBuilder Build(bool listens)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(x =>
            {
                x.SetMinimumLevel(LogLevel.Information);
                // Wall-clock timestamps, so one envelope can be followed across the worker logs
                x.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss.fff ");
                x.AddFilter("Wolverine.Runtime.WolverineRuntime", LogLevel.Warning);
                x.AddFilter("Microsoft", LogLevel.Warning);
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "chaos";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(Store switch
                    {
                        "ef" or "efpg" => typeof(EfWorkHandler),
                        "mysql" or "oracle" or "sqlite" => typeof(AdoWorkHandler),
                        _ => typeof(MartenWorkHandler)
                    });

                // CHAOS_DURABILITY=solo for a store meant for one node, such as SQLite
                opts.Durability.Mode = Environment.GetEnvironmentVariable("CHAOS_DURABILITY") == "solo"
                    ? DurabilityMode.Solo
                    : DurabilityMode.Balanced;
                opts.Durability.FirstHealthCheckExecution = 1.Seconds();
                opts.Durability.HealthCheckPollingTime = 2.Seconds();
                opts.Durability.StaleNodeTimeout = 10.Seconds();
                opts.Durability.CheckAssignmentPeriod = 5.Seconds();
                opts.Durability.NodeReassignmentPollingTime = 2.Seconds();
                opts.Durability.ScheduledJobFirstExecution = 1.Seconds();
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();

                if (Store == "ef")
                {
                    opts.UseSqlServerPersistenceAndTransport(SqlServerConnectionString, Schema).AutoProvision();
                    opts.Services.AddDbContextWithWolverineIntegration<ChaosDbContext>(x =>
                        x.UseSqlServer(SqlServerConnectionString));
                    opts.UseEntityFrameworkCoreTransactions();
                    opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                }
                else if (Store == "efpg")
                {
                    opts.UsePostgresqlPersistenceAndTransport(Db.NodeConnectionString, Schema,
                        transportSchema: Schema + "_queues").AutoProvision();
                    opts.Services.AddDbContextWithWolverineIntegration<ChaosDbContext>(x =>
                        x.UseNpgsql(Db.NodeConnectionString));
                    opts.UseEntityFrameworkCoreTransactions();
                    opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                }
                else if (Store == "mysql")
                {
                    opts.UseMySqlPersistenceAndTransport(Db.NodeConnectionString, Schema, Schema + "_queues")
                        .AutoProvision();
                }
                else if (Store == "oracle")
                {
                    opts.PersistMessagesWithOracle(Db.NodeConnectionString, Db.Schema).EnableMessageTransport();
                }
                else if (Store == "sqlite")
                {
                    opts.UseSqlitePersistenceAndTransport(Db.NodeConnectionString).AutoProvision();
                }
                else
                {
                    opts.Services.AddMarten(m =>
                        {
                            if (Tenancy.Enabled)
                            {
                                // CHAOS_TENANTS=1: a database per tenant, the second one in its own container
                                // so a dbdown fault takes one tenant's inbox away while the other keeps working
                                m.MultiTenantedDatabases(x =>
                                {
                                    foreach (var (tenant, connectionString) in Tenancy.Databases)
                                    {
                                        x.AddSingleTenantDatabase(connectionString, tenant);
                                    }
                                });
                            }
                            else
                            {
                                m.Connection(Db.NodeConnectionString);
                            }

                            m.DatabaseSchemaName = Schema;
                            m.Schema.For<Processed>().Duplicate(x => x.MessageId).Duplicate(x => x.Stage);
                            m.Schema.For<Sent>().Duplicate(x => x.Flags);
                        })
                        .UseLightweightSessions()
                        .IntegrateWithWolverine(x =>
                        {
                            x.TransportSchemaName = Schema + "_queues";
                            if (Tenancy.Enabled) x.MainDatabaseConnectionString = Db.NodeConnectionString;
                        });
                }

                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();

                opts.OnException<FlakyException>().ScheduleRetry(RetryCooldown);

                switch (Transport)
                {
                    case "rabbit":
                        opts.UseRabbitMq().AutoProvision();
                        opts.PublishMessage<Work>().ToRabbitQueue("chaos-work");
                        if (listens) opts.ListenToRabbitQueue("chaos-work").UseDurableInbox().MaximumParallelMessages(8);
                        break;

                    case "pgshard":
                        // Global partitioning over four sharded PostgreSQL queues. The leader hands the slots out to
                        // the nodes and moves them when a node dies, which is what this mode is for
                        opts.MessagePartitioning.ByMessage<Work>(x => x.Id.ToString());
                        opts.MessagePartitioning.GlobalPartitioned(topology =>
                        {
                            topology.UseShardedPostgresqlQueues("chaos_shard", 4);
                            topology.Message<Work>();
                        });
                        break;

                    case "mysqlqueue":
                        opts.PublishMessage<Work>().ToMySqlQueue("chaos_work");
                        if (listens) opts.ListenToMySqlQueue("chaos_work").UseDurableInbox().MaximumParallelMessages(8);
                        break;

                    case "oraclequeue":
                        opts.PublishMessage<Work>().ToOracleQueue("chaos_work");
                        if (listens) opts.ListenToOracleQueue("chaos_work").UseDurableInbox().MaximumParallelMessages(8);
                        break;

                    case "sqlitequeue":
                        opts.PublishMessage<Work>().ToSqliteQueue("chaos_work");
                        if (listens) opts.ListenToSqliteQueue("chaos_work").UseDurableInbox().MaximumParallelMessages(8);
                        break;

                    case "sqlqueue":
                        opts.PublishMessage<Work>().ToSqlServerQueue("chaos_work");
                        if (listens) opts.ListenToSqlServerQueue("chaos_work").UseDurableInbox().MaximumParallelMessages(8);
                        break;

                    default:
                        opts.PublishMessage<Work>().ToPostgresqlQueue("chaos_work");
                        if (listens) opts.ListenToPostgresqlQueue("chaos_work").UseDurableInbox().MaximumParallelMessages(8);
                        break;
                }

                opts.PublishMessage<LocalStep>().ToLocalQueue("chaos-local");
                opts.LocalQueue("chaos-local").UseDurableInbox().MaximumParallelMessages(4);

                opts.PublishMessage<DelayedStep>().ToLocalQueue("chaos-delayed");
                opts.LocalQueue("chaos-delayed").UseDurableInbox();
            });
    }

    public static async Task RunWorkerAsync()
    {
        using var host = Build(listens: true).Build();
        await host.StartAsync();

        // Which node number this incarnation got, so "ejected stale node N" in another log can be matched to it
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {Name} started as node {runtime.Options.Durability.AssignedNodeNumber} ({runtime.Options.UniqueNodeId})");

        await host.WaitForShutdownAsync();
    }

    /// <summary>
    /// Commits batches of <see cref="Sent"/> rows and their <see cref="Work"/> messages through the outbox until it is
    /// stopped or killed. A batch is in the ledger exactly when its outbox rows are
    /// </summary>
    public static async Task RunProducerAsync()
    {
        using var host = Build(listens: false).Build();
        await host.StartAsync();

        var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        var stopFile = Path.Combine(Dir, "stop-producing");
        var poisonShare = double.TryParse(Environment.GetEnvironmentVariable("CHAOS_POISON"), out var p) ? p : 0;

        while (!stopping.IsCancellationRequested && !File.Exists(stopFile))
        {
            var batch = Enumerable.Range(0, 10).Select(_ =>
            {
                var flags = Random.Shared.Next(0, 8);
                if (Random.Shared.NextDouble() < poisonShare) flags |= Flags.Poison;
                return new Sent { Id = Guid.NewGuid(), Flags = flags, At = DateTimeOffset.UtcNow };
            }).ToArray();

            try
            {
                switch (Store)
                {
                    case "ef" or "efpg":
                        await commitWithEfAsync(host, batch, stopping);
                        break;
                    case "mysql" or "oracle" or "sqlite":
                        await commitWithAdoAsync(host, batch, stopping);
                        break;
                    default:
                        await commitWithMartenAsync(host, batch, stopping);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                // A database outage: the batch either committed whole or not at all, and the ledger says which
                Console.WriteLine($"producer batch failed: {e.GetType().Name}: {e.Message}");
                await Task.Delay(500);
            }

            await Task.Delay(50);
        }

        await host.StopAsync();
    }

    private static async Task commitWithMartenAsync(IHost host, Sent[] batch, CancellationToken token)
    {
        var factory = host.Services.GetRequiredService<OutboxedSessionFactory>();
        var context = new MessageContext(host.Services.GetRequiredService<IWolverineRuntime>());

        // With tenancy, each batch goes to one tenant's database, alternating, and its messages carry that tenant
        if (Tenancy.Enabled) context.TenantId = Tenancy.Next();
        await using var session = factory.OpenSession(context);
        foreach (var sent in batch)
        {
            session.Store(sent);
            await context.PublishAsync(new Work(sent.Id, sent.Flags));
        }

        await session.SaveChangesAsync(token);
    }

    /// <summary>The documented outbox pattern on a plain connection: enlist, write, publish, commit, then flush</summary>
    private static async Task commitWithAdoAsync(IHost host, Sent[] batch, CancellationToken token)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var database = (IMessageDatabase)runtime.Storage;
        var context = new MessageContext(runtime);

        await using var conn = Db.Open(Db.NodeConnectionString);
        await conn.OpenAsync(token);
        await using var tx = await conn.BeginTransactionAsync(token);
        await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(database, tx));

        foreach (var sent in batch)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"insert into {Db.SentTable} (id, flags, at) values ({AdoWorkHandler.p(0)}, {AdoWorkHandler.p(1)}, {AdoWorkHandler.p(2)})";
            AdoWorkHandler.add(cmd, 0, Db.Id(sent.Id));
            AdoWorkHandler.add(cmd, 1, sent.Flags);
            AdoWorkHandler.add(cmd, 2, Db.Timestamp(sent.At));
            await cmd.ExecuteNonQueryAsync(token);

            await context.PublishAsync(new Work(sent.Id, sent.Flags));
        }

        await tx.CommitAsync(token);
        await context.FlushOutgoingMessagesAsync();
    }

    private static async Task commitWithEfAsync(IHost host, Sent[] batch, CancellationToken token)
    {
        using var scope = host.Services.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<ChaosDbContext>>();
        foreach (var sent in batch)
        {
            outbox.DbContext.Sent.Add(sent);
            await outbox.PublishAsync(new Work(sent.Id, sent.Flags));
        }

        await outbox.SaveChangesAndFlushMessagesAsync(token);
    }
}

public static class Ledger
{
    public static Processed Record(Guid messageId, string stage, Envelope envelope) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = messageId,
        Stage = stage,
        Node = Node.Name,
        Attempts = envelope.Attempts,
        At = DateTimeOffset.UtcNow,
        EnvelopeId = envelope.Id.ToString("D")
    };
}
