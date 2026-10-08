using Bobcat.Engine;
using IntegrationTests;
using JasperFx;
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
using FisherStore = global::Fisher.IDocumentStore;
using MartenStore = global::Marten.IDocumentStore;
using PolecatStore = global::Polecat.IDocumentStore;

namespace Wolverine.Bobcat.Tests.AggregateLess;

// GH-4892: Marten, Polecat and Fisher all allow an event stream with no aggregate type, and an event
// model that names no aggregate generates exactly that. GivenEvents(id, ...) arranges such a stream,
// and the handler appends to it with Storage.AppendEvents -- on every store.

public record CallStarted(Guid Id, string Caller);

public record LogCall(Guid Id, string Note);

public record CallLogged(Guid Id, string Note);

public static class LogCallHandler
{
    [Emits(typeof(CallLogged))]
    public static AppendEvents Handle(LogCall command) => Storage.AppendEvents(command.Id, new CallLogged(command.Id, command.Note));
}

public abstract class AggregateLessHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    protected abstract void ConfigureStore(WolverineOptions opts);

    protected virtual Task AfterStartAsync() => Task.CompletedTask;

    public abstract Task<(int Count, Type? AggregateType)> StreamAsync(Guid id);

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LogCallHandler));
                ConfigureStore(opts);
            })
            .StartAsync();

        await AfterStartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public sealed class MartenAggregateLessHost : AggregateLessHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "bobcat_aggregate_less";
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();

    public override async Task<(int Count, Type? AggregateType)> StreamAsync(Guid id)
    {
        await using var session = Host.Services.GetRequiredService<MartenStore>().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);
        var state = await session.Events.FetchStreamStateAsync(id);
        return (events.Count, state?.AggregateType);
    }
}

public sealed class PolecatAggregateLessHost : AggregateLessHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "bobcat_aggregate_less";
        }).IntegrateWithWolverine();

    protected override Task AfterStartAsync()
        => ((global::Polecat.DocumentStore)Host.Services.GetRequiredService<PolecatStore>()).Database.ApplyAllConfiguredChangesToDatabaseAsync();

    public override async Task<(int Count, Type? AggregateType)> StreamAsync(Guid id)
    {
        await using var session = Host.Services.GetRequiredService<PolecatStore>().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);
        var state = await session.Events.FetchStreamStateAsync(id);
        return (events.Count, state?.AggregateType);
    }
}

public sealed class FisherAggregateLessHost : AggregateLessHost
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bobcat_aggregate_less_{Guid.NewGuid():N}.db");

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddFisher(o =>
            {
                o.Connection($"Data Source={_file}");
                o.AutoCreateSchemaObjects = AutoCreate.All;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();

    public override async Task<(int Count, Type? AggregateType)> StreamAsync(Guid id)
    {
        await using var session = Host.Services.GetRequiredService<FisherStore>().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);
        var state = await session.Events.FetchStreamStateAsync(id);
        return (events.Count, state?.AggregateType);
    }
}

public abstract class aggregate_less_streams<THost>(THost app) : WolverineSpec(app.Host)
    where THost : AggregateLessHost
{
    [Fact]
    public async Task given_events_by_id_alone_arranges_a_stream_with_no_aggregate_type()
    {
        var theCall = Guid.CreateVersion7();

        var recording = await Recordings.RecordAsync(async () =>
        {
            // binds GivenEvents(Guid, params object[]) -- there is no aggregate to name
            await GivenEvents(theCall, new CallStarted(theCall, "Ann"));
            await WhenReceived(new LogCall(theCall, "asked about fostering"));
            ThenEvents(typeof(CallLogged));
        });

        recording.Steps.First().Text.ShouldBe("stream theCall has already recorded CallStarted(Id: theCall, Caller: \"Ann\")");
        recording.Steps.ShouldAllBe(x => x.Status == ResultStatus.success);
        recording.GatheredFailures().ShouldBeNull();

        var (count, aggregateType) = await app.StreamAsync(theCall);
        count.ShouldBe(2);
        aggregateType.ShouldBeNull();
    }

    [Fact]
    public async Task given_events_on_an_existing_aggregate_less_stream_appends()
    {
        var id = Guid.CreateVersion7();

        await GivenEvents(id, new CallStarted(id, "Ann"));
        await GivenEvents(id, new CallLogged(id, "again"));

        (await app.StreamAsync(id)).Count.ShouldBe(2);
    }
}

public sealed class marten_aggregate_less_streams(MartenAggregateLessHost app)
    : aggregate_less_streams<MartenAggregateLessHost>(app), IClassFixture<MartenAggregateLessHost>;

public sealed class polecat_aggregate_less_streams(PolecatAggregateLessHost app)
    : aggregate_less_streams<PolecatAggregateLessHost>(app), IClassFixture<PolecatAggregateLessHost>;

public sealed class fisher_aggregate_less_streams(FisherAggregateLessHost app)
    : aggregate_less_streams<FisherAggregateLessHost>(app), IClassFixture<FisherAggregateLessHost>;
