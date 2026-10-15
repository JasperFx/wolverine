using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;

// PR #5044 exploratory testing: one "serverless function instance" under DurabilityMode.ServerlessWithOutbox,
// started by a test with `dotnet ServerlessOutboxCrashHost.dll <store> <schema> <mode> [args]` and, at the point
// the test cares about, killed with SIGKILL. Everything the test reads from it is a line starting with "@@".
//
//   store:  marten | postgresql | sqlserver | efcore (EF Core on SQL Server)
//   modes:  setup                     build the schema and clear every envelope table
//           send <port> <id> [delayMs] [count]
//                                     run a transactional handler that commits business data and cascades an
//                                     ExplorePing to tcp://localhost:<port>, which is sent inline after the commit.
//                                     With delayMs > 0 the ping is scheduled (DelayedFor) instead. With count > 1,
//                                     that many handlers run concurrently with ids derived from <id>
//           pass                      run one durability pass and print its report
//           listen <port> <block>     start a durable TCP listener by hand. With block=true the ExplorePing
//                                     handler never finishes
//
// Env: EXPLORE_OUTBOX_STALE_MS / EXPLORE_INBOX_STALE_MS override the stale times. EXPLORE_PASS_AT (unix ms) makes
// `pass` wait for that instant after startup, so two instances run their passes at the same moment

var store = args[0];
var schema = args[1];
var mode = args[2];

int? sendPort = mode == "send" ? int.Parse(args[3]) : null;
int? listenPort = mode == "listen" ? int.Parse(args[3]) : null;
ExplorePingHandler.Block = mode == "listen" && args.Length > 4 && bool.Parse(args[4]);

var builder = Host.CreateDefaultBuilder();
builder.ConfigureLogging(logging =>
{
    logging.ClearProviders();
    logging.AddSimpleConsole(o => o.SingleLine = true);
    logging.SetMinimumLevel(LogLevel.Warning);
    logging.AddFilter("Wolverine", LogLevel.Information);
});

builder.UseWolverine(opts =>
{
    opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

    if (int.TryParse(Environment.GetEnvironmentVariable("EXPLORE_OUTBOX_STALE_MS"), out var outboxMs))
    {
        opts.Durability.OutboxStaleTime = outboxMs.Milliseconds();
    }

    if (int.TryParse(Environment.GetEnvironmentVariable("EXPLORE_INBOX_STALE_MS"), out var inboxMs))
    {
        opts.Durability.InboxStaleTime = inboxMs.Milliseconds();
    }

    var discovery = opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ExplorePingHandler));

    switch (store)
    {
        case "marten":
            discovery.IncludeType(typeof(MartenExploreHandler));
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = schema;
                    m.DisableNpgsqlLogging = true;
                })
                .IntegrateWithWolverine(x => x.MessageStorageSchemaName = schema);
            break;

        case "postgresql":
            discovery.IncludeType(typeof(ExploreSaga));
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
            opts.AddSagaType<ExploreSaga>();
            break;

        case "sqlserver":
            discovery.IncludeType(typeof(ExploreSaga));
            opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, schema);
            opts.AddSagaType<ExploreSaga>();
            break;

        case "efcore":
            discovery.IncludeType(typeof(EfCoreExploreHandler));
            ExploreDbContext.Schema = schema;
            opts.Services.AddDbContextWithWolverineIntegration<ExploreDbContext>(o =>
                o.UseSqlServer(Servers.SqlServerConnectionString));
            opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, schema);
            opts.UseEntityFrameworkCoreTransactions();
            opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            break;

        default:
            throw new ArgumentOutOfRangeException(nameof(store), store, null);
    }

    opts.Policies.AutoApplyTransactions();

    if (sendPort.HasValue) opts.PublishMessage<ExplorePing>().ToPort(sendPort.Value);
    if (listenPort.HasValue) opts.ListenAtPort(listenPort.Value);

    opts.Services.AddResourceSetupOnStartup();
});

using var host = builder.Build();
await host.StartAsync();

Console.WriteLine($"@@STARTED pid={Environment.ProcessId}");

switch (mode)
{
    case "setup":
        await host.ResetResourceState();
        Console.WriteLine("@@READY");
        break;

    case "send":
    {
        var id = Guid.Parse(args[4]);
        var delayMs = args.Length > 5 ? int.Parse(args[5]) : 0;
        var count = args.Length > 6 ? int.Parse(args[6]) : 1;

        var ids = Enumerable.Range(0, count).Select(i => count == 1 ? id : ExploreIds.Derive(id, i)).ToArray();
        await Task.WhenAll(ids.Select(async x =>
        {
            object command = store is "postgresql" or "sqlserver"
                ? new ExploreStart(x, delayMs)
                : new ExploreCommand(x, delayMs);

            Console.WriteLine($"@@INVOKING {x}");
            await host.MessageBus().InvokeAsync(command);
            Console.WriteLine($"@@DONE {x}");
        }));
        break;
    }

    case "pass":
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("EXPLORE_PASS_AT"), out var at))
        {
            var wait = DateTimeOffset.FromUnixTimeMilliseconds(at) - DateTimeOffset.UtcNow;
            Console.WriteLine($"@@WAITING {wait.TotalMilliseconds:F0}ms");
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
        }

        Console.WriteLine($"@@PASSING {DateTimeOffset.UtcNow:O}");
        var report = await host.RunDurabilityPassAsync(30.Seconds());
        Console.WriteLine(
            $"@@PASS sent={report.OutgoingSent} failed={report.OutgoingFailed} scheduled={report.ScheduledReleased} incoming={report.IncomingRecovered} skipped={report.SkippedStores.Count} failedSteps={report.FailedSteps} remaining={report.Remaining}");
        break;
    }

    case "listen":
    {
        var runtime = host.GetRuntime();
        var endpoint = runtime.Options.Transports.AllEndpoints()
            .Single(x => x.IsListener && x.Uri.Port == listenPort);
        await runtime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);
        Console.WriteLine($"@@LISTENING {endpoint.Uri} mode={endpoint.Mode}");

        // A function instance that stays up until it is killed
        await Task.Delay(Timeout.Infinite);
        break;
    }
}

await host.StopAsync();
Console.WriteLine("@@EXIT");

// The receiving test host maps the same identity onto its own type
[MessageIdentity("explore5044-ping")]
public record ExplorePing(Guid Id);

public record ExploreCommand(Guid Id, int DelayMs);

public record ExploreStart(Guid Id, int DelayMs);

public static class ExploreIds
{
    // Same derivation as the test side, so the test knows every id a multi-send used
    public static Guid Derive(Guid seed, int index)
    {
        var bytes = seed.ToByteArray();
        var suffix = BitConverter.GetBytes(index);
        for (var i = 0; i < 4; i++) bytes[12 + i] = suffix[i];
        return new Guid(bytes);
    }

    public static OutgoingMessages PingFor(Guid id, int delayMs)
    {
        var messages = new OutgoingMessages();
        if (delayMs > 0)
        {
            messages.Delay(new ExplorePing(id), delayMs.Milliseconds());
        }
        else
        {
            messages.Add(new ExplorePing(id));
        }

        return messages;
    }
}

public class ExploreDoc
{
    public Guid Id { get; set; }
}

public static class MartenExploreHandler
{
    public static OutgoingMessages Handle(ExploreCommand command, IDocumentSession session)
    {
        session.Store(new ExploreDoc { Id = command.Id });
        return ExploreIds.PingFor(command.Id, command.DelayMs);
    }
}

public class ExploreItem
{
    public Guid Id { get; set; }
}

public class ExploreDbContext : DbContext
{
    public static string Schema = "explore5044_rec_ef";

    public ExploreDbContext(DbContextOptions<ExploreDbContext> options) : base(options)
    {
    }

    public DbSet<ExploreItem> Items => Set<ExploreItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExploreItem>(map =>
        {
            map.ToTable("explore_items", Schema);
            map.HasKey(x => x.Id);
            map.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        });
    }
}

public static class EfCoreExploreHandler
{
    public static OutgoingMessages Handle(ExploreCommand command, ExploreDbContext db)
    {
        db.Items.Add(new ExploreItem { Id = command.Id });
        return ExploreIds.PingFor(command.Id, command.DelayMs);
    }
}

// The lightweight RDBMS saga storage is the plain PostgreSQL / SQL Server transactional handler path: the saga row
// and the outbox rows commit together, then FlushOutgoingMessages sends inline
public class ExploreSaga : Saga
{
    public Guid Id { get; set; }

    public static (ExploreSaga, OutgoingMessages) Start(ExploreStart start)
    {
        return (new ExploreSaga { Id = start.Id }, ExploreIds.PingFor(start.Id, start.DelayMs));
    }
}

public static class ExplorePingHandler
{
    public static bool Block;

    public static async Task Handle(ExplorePing ping)
    {
        Console.WriteLine($"@@HANDLING {ping.Id}");
        if (Block) await Task.Delay(Timeout.Infinite);
        Console.WriteLine($"@@HANDLED {ping.Id}");
    }
}
