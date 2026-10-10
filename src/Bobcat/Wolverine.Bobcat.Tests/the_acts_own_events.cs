using Bobcat.Engine;
using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Fisher;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Polecat;
using Fisher;
using Marten;
using Polecat;
using PolecatStore = global::Polecat.IDocumentStore;

namespace Wolverine.Bobcat.Tests.TheActsOwnEvents;

// GH-4931: what an act appended is what the tracked session heard each event-store session commit, never
// read back from the store. ThenEvents is the act's own -- what the session handling its message committed
// -- and a cascade's events are only in AllNewEvents, where ThenEventsOn looks. A stream the act's handler
// started is named by TheStartedStream<T>(). On Marten, Polecat and Fisher.

public record OpenTab(string Name);
public record CloseTab(Guid TabId);
public record ArchiveTab(Guid TabId);

public record TabOpened(string Name);
public record TabClosed;
public record TabArchived;

public class Tab
{
    public Guid Id { get; set; }
    public static Tab Create(TabOpened e) => new();
    public void Apply(TabClosed e) { }
    public void Apply(TabArchived e) { }
}

public static class TabHandlers
{
    public static StartStream Handle(OpenTab command)
        => Storage.StartStream<Tab>(Guid.CreateVersion7(), new TabOpened(command.Name));

    public static ArchiveTab Handle(
        CloseTab command,
        [WriteModel(nameof(CloseTab.TabId))] IEventStream<Tab> tab)
    {
        tab.AppendOne(new TabClosed());
        return new ArchiveTab(command.TabId);
    }

    public static void Handle(
        ArchiveTab command,
        [WriteModel(nameof(ArchiveTab.TabId))] IEventStream<Tab> tab)
        => tab.AppendOne(new TabArchived());
}

public abstract class TabHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    protected abstract void ConfigureStore(WolverineOptions opts);

    protected virtual Task AfterStartAsync() => Task.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TabHandlers));
                ConfigureStore(opts);
            })
            .StartAsync();

        await AfterStartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // A host that failed to start has nothing to stop; let the startup failure be the one reported
        if (Host is null) return;

        await Host.StopAsync();
        Host.Dispose();
    }
}

public sealed class MartenTabHost : TabHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "bobcat_the_acts_own_events";
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();
}

public sealed class PolecatTabHost : TabHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "bobcat_the_acts_own_events";
        }).IntegrateWithWolverine();

    protected override Task AfterStartAsync()
        => ((global::Polecat.DocumentStore)Host.Services.GetRequiredService<PolecatStore>()).Database.ApplyAllConfiguredChangesToDatabaseAsync();
}

public sealed class FisherTabHost : TabHost
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bobcat_the_acts_own_events_{Guid.NewGuid():N}.db");

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddFisher(o =>
            {
                o.Connection($"Data Source={_file}");
                o.AutoCreateSchemaObjects = AutoCreate.All;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
}

public abstract class the_acts_own_events<THost>(THost app) : WolverineSpec(app.Host)
    where THost : TabHost
{
    [Fact]
    public async Task a_stream_the_handler_started_is_named_by_the_id_it_assigned()
    {
        await WhenReceived(new OpenTab("the regulars"));

        var theTab = TheStartedStream<Tab>();
        theTab.ShouldNotBe(Guid.Empty);

        ThenEvents(new TabOpened("the regulars"));
        await ThenStreamIsStarted<Tab>(theTab);
    }

    [Fact]
    public async Task a_started_stream_and_its_events_read_as_one_paragraph_of_two_sentences()
    {
        // wolverine#4865: Storyteller's paragraph -- one call, two sentences, each with its own verdict
        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new OpenTab("the regulars"));
            await ThenStreamIsStartedWithEvents<Tab>(new TabOpened("the regulars"));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Skip(1).Select(x => $"{x.Keyword} {x.Text}").ShouldBe([
            "Then a Tab stream is started",
            "And TabOpened is emitted on it"
        ]);

        // The second sentence fails on its own
        var failure = await Should.ThrowAsync<Exception>(() => ThenStreamIsStartedWithEvents<Tab>(new TabOpened("someone else")));
        failure.Message.ShouldContain("Name");
    }

    [Fact]
    public async Task then_events_is_the_acts_own_and_a_cascades_events_are_not()
    {
        var theTab = Guid.CreateVersion7();
        await GivenEvents<Tab>(theTab, new TabOpened("the regulars"));

        await WhenReceived(new CloseTab(theTab));

        // The act's own: what the session handling CloseTab committed
        ThenEvents(new TabClosed());

        // The stream's share of everything the act caused, the cascade's ArchiveTab included
        ThenEventsOn<Tab>(theTab, new TabClosed(), new TabArchived());
        Scenario.LastAct.AllNewEvents.Select(x => x.Data.GetType().Name).ShouldBe(["TabClosed", "TabArchived"]);
    }

    [Fact]
    public async Task arranged_history_is_never_the_acts()
    {
        var theTab = Guid.CreateVersion7();
        await GivenEvents<Tab>(theTab, new TabOpened("the regulars"));

        await WhenReceived(new CloseTab(theTab));

        Scenario.LastAct.AllNewEvents.ShouldNotContain(x => x.Data is TabOpened);
        Scenario.LastAct.StartedStreams.ShouldBeEmpty();
    }

    [Fact]
    public async Task naming_a_stream_the_act_did_not_start_says_what_it_did()
    {
        var theTab = Guid.CreateVersion7();
        await GivenEvents<Tab>(theTab, new TabOpened("the regulars"));

        await WhenReceived(new CloseTab(theTab));

        var failure = Should.Throw<SpecCriticalException>(() => TheStartedStream<Tab>());
        failure.Message.ShouldContain("started no stream at all");
    }
}

public sealed class marten_the_acts_own_events(MartenTabHost app)
    : the_acts_own_events<MartenTabHost>(app), IClassFixture<MartenTabHost>;

public sealed class polecat_the_acts_own_events(PolecatTabHost app)
    : the_acts_own_events<PolecatTabHost>(app), IClassFixture<PolecatTabHost>;

public sealed class fisher_the_acts_own_events(FisherTabHost app)
    : the_acts_own_events<FisherTabHost>(app), IClassFixture<FisherTabHost>;
