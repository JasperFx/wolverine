using System.Diagnostics;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

// An application's main Marten store beside an ancillary store it does not own, the way an embedded CritterWatch
// console registers its own (AddMartenStore<ICritterWatchStore>). The scenario names no store: the main store is
// the default, and the console's store — whose async projection never catches up here, because nothing runs its
// daemon — holds up no wait.

/// <summary>The ancillary store's marker, standing in for CritterWatch's <c>ICritterWatchStore</c>.</summary>
public interface IConsoleStore : Marten.IDocumentStore;

public record ConsoleEntryRecorded(Guid EntryId, string Text);

/// <summary>The console store's async read model. Nothing runs that store's daemon, so it is always behind.</summary>
public class ConsoleSummary
{
    public Guid Id { get; set; }
    public int Entries { get; set; }

    public void Apply(ConsoleEntryRecorded e)
    {
        Id = e.EntryId;
        Entries++;
    }
}

public sealed class MartenMainAndConsoleHost : StoreHost
{
    private static int _hosts;
    private readonly string _schema = $"bobcat_cs_main_{Interlocked.Increment(ref _hosts)}";

    public override bool IsAvailable => PostgresEnvironment.IsCi || PostgresEnvironment.IsAvailable;

    public override string SkipReason => PostgresEnvironment.SkipReason;

    protected override IHost Build()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddMarten(opts =>
            {
                opts.Connection(PostgresEnvironment.ConnectionString);
                opts.DatabaseSchemaName = _schema;
                opts.AutoCreateSchemaObjects = AutoCreate.All;
                opts.DisableNpgsqlLogging = true;

                opts.Projections.Snapshot<Wallet>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<WalletSummary>(SnapshotLifecycle.Async);
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);

        // No AddAsyncDaemon: this store's async projection never moves
        builder.Services.AddMartenStore<IConsoleStore>(opts =>
            {
                opts.Connection(PostgresEnvironment.ConnectionString);
                opts.DatabaseSchemaName = _schema + "_console";
                opts.AutoCreateSchemaObjects = AutoCreate.All;
                opts.DisableNpgsqlLogging = true;

                opts.Projections.Snapshot<ConsoleSummary>(SnapshotLifecycle.Async);
            })
            .ApplyAllDatabaseChangesOnStartup();

        return builder.Build();
    }

    /// <summary>Put the console store behind: an event its never-running projection will never reach.</summary>
    public async Task PutTheConsoleBehindAsync()
    {
        var console = Host.Services.GetRequiredService<IConsoleStore>();
        await using var session = console.LightweightSession();
        var entry = Guid.NewGuid();
        session.Events.StartStream<ConsoleSummary>(entry, new ConsoleEntryRecorded(entry, "the console's own traffic"));
        await session.SaveChangesAsync();
    }

    protected override async Task BeforeStartAsync()
    {
        await MartenDdl.Gate.WaitAsync();
        await dropSchemasAsync();
    }

    protected override void AfterStartAttempt() => MartenDdl.Gate.Release();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (IsAvailable) await dropSchemasAsync();
    }

    private async Task dropSchemasAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(PostgresEnvironment.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {_schema} CASCADE; DROP SCHEMA IF EXISTS {_schema}_console CASCADE";
        await command.ExecuteNonQueryAsync();
    }
}

public class main_store_default_on_marten(MartenMainAndConsoleHost host) : IClassFixture<MartenMainAndConsoleHost>
{
    private CritterStackScenario newScenario()
    {
        Assert.SkipUnless(host.IsAvailable, host.SkipReason);
        return new CritterStackScenario(host.Host) { ProjectionTimeout = TimeSpan.FromSeconds(10) };
    }

    private IEventStore main => host.Host.Services.EventStore("main");

    [Fact]
    public async Task an_unqualified_specification_uses_the_main_store_and_its_waits_ignore_the_console_store()
    {
        var scenario = newScenario();
        await host.PutTheConsoleBehindAsync();

        host.Host.Services.EventStores().Count.ShouldBe(2);
        host.Host.Services.EventStore().ShouldBeSameAs(main);
        scenario.EventStore.ShouldBeSameAs(main);

        var wallet = Guid.NewGuid();
        var stray = Guid.NewGuid();
        var clock = Stopwatch.StartNew();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenEvents<Wallet>(wallet, new WalletOpened(wallet, "Di"));
            // A document no store registers, and a stream named by its id alone: both go to the main store
            await scenario.GivenReadModel<Stray>(new Stray { Id = stray });

            await scenario.WhenActing("the wallet is credited",
                services => EventStoreAuthoring.AppendAsync(services.EventStore(), typeof(Wallet), wallet, [new WalletCredited(wallet, 7m)]));

            scenario.ThenEvents(new WalletCredited(wallet, 7m));
            await scenario.AfterAsyncDaemonsCatchUpAsync();
            await scenario.ThenReadModel<WalletSummary>(wallet, Specifications.Specify<WalletSummary>().With(x => x.Credits, 1));
            await scenario.ThenAggregate<Wallet>(wallet, Specifications.Specify<Wallet>().With(x => x.Owner, "Di"));
        });

        recording.GatheredFailures().ShouldBeNull();
        (await scenario.TheStreamVersion(wallet)).ShouldBe(2);
        (await EventStoreAuthoring.LoadDocumentAsync<Stray>(main, stray)).ShouldNotBeNull();

        // Well inside one console-store timeout: the console store was never waited on
        clock.Elapsed.ShouldBeLessThan(scenario.ProjectionTimeout);

        // ...and it would have stalled the wait: addressed explicitly, it times out
        var console = scenario.In<IConsoleStore>();
        console.ProjectionTimeout = TimeSpan.FromSeconds(1);
        await Should.ThrowAsync<TimeoutException>(() => console.AfterAsyncDaemonsCatchUpAsync());
    }
}
