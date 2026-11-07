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
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
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
        // Widen the in-flight window a kill can land in
        await Task.Delay(Random.Shared.Next(0, 40));

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
        });
    }
}

public static class Node
{
    public static string Schema => Environment.GetEnvironmentVariable("CHAOS_SCHEMA") ?? "chaos";

    /// <summary>pgqueue, rabbit or sqlqueue</summary>
    public static string Transport => Environment.GetEnvironmentVariable("CHAOS_TRANSPORT") ?? "pgqueue";

    /// <summary>marten (PostgreSQL) or ef (EF Core over SQL Server)</summary>
    public static string Store => Environment.GetEnvironmentVariable("CHAOS_STORE") ?? "marten";

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
                x.AddFilter("Wolverine.Runtime.WolverineRuntime", LogLevel.Warning);
                x.AddFilter("Microsoft", LogLevel.Warning);
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "chaos";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(Store == "ef" ? typeof(EfWorkHandler) : typeof(MartenWorkHandler));

                opts.Durability.Mode = DurabilityMode.Balanced;
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
                else
                {
                    opts.Services.AddMarten(m =>
                        {
                            m.Connection(PostgresConnectionString);
                            m.DatabaseSchemaName = Schema;
                            m.Schema.For<Processed>().Duplicate(x => x.MessageId).Duplicate(x => x.Stage);
                            m.Schema.For<Sent>().Duplicate(x => x.Flags);
                        })
                        .UseLightweightSessions()
                        .IntegrateWithWolverine(x => x.TransportSchemaName = Schema + "_queues");
                }

                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();

                opts.OnException<FlakyException>().ScheduleRetry(1.Seconds());

                switch (Transport)
                {
                    case "rabbit":
                        opts.UseRabbitMq().AutoProvision();
                        opts.PublishMessage<Work>().ToRabbitQueue("chaos-work");
                        if (listens) opts.ListenToRabbitQueue("chaos-work").UseDurableInbox().MaximumParallelMessages(8);
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
        await host.RunAsync();
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
                if (Store == "ef")
                {
                    await commitWithEfAsync(host, batch, stopping);
                }
                else
                {
                    await commitWithMartenAsync(host, batch, stopping);
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
        await using var session = factory.OpenSession(context);
        foreach (var sent in batch)
        {
            session.Store(sent);
            await context.PublishAsync(new Work(sent.Id, sent.Flags));
        }

        await session.SaveChangesAsync(token);
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
        At = DateTimeOffset.UtcNow
    };
}
