using System.Diagnostics.CodeAnalysis;
using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow.LoneId4883;

// GH-4883: a scaffolded stub command names the one stream it identifies `Id`, in Guid mode and strong-typed mode
// alike. Every aggregate workflow attribute has to find the stream by that lone Id, by convention:
// [AggregateHandler], [WriteAggregate], [ReadAggregate], and the store-agnostic [WriteModel] / [ReadModel].

public readonly record struct KettleId(Guid Value) : IParsable<KettleId>
{
    public static KettleId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out KettleId result)
    {
        var parsed = Guid.TryParse(s, provider, out var value);
        result = new KettleId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString();
}

public record KettleFilled(int Cups);
public record KettleBoiled;

public class Kettle
{
    public Guid Id { get; set; }
    public int Boils { get; set; }
    public void Apply(KettleFilled e) { }
    public void Apply(KettleBoiled e) => Boils++;
}

public class StrongKettle
{
    public KettleId Id { get; set; }
    public int Boils { get; set; }
    public void Apply(KettleFilled e) { }
    public void Apply(KettleBoiled e) => Boils++;
}

// Guid mode
public record BoilByHandler(Guid Id);
public record BoilByWriteAggregate(Guid Id);
public record BoilByWriteModel(Guid Id);
public record CountByReadAggregate(Guid Id);
public record CountByReadModel(Guid Id);

// Strong-typed mode
public record BoilStrongByHandler(KettleId Id);
public record BoilStrongByWriteAggregate(KettleId Id);
public record BoilStrongByWriteModel(KettleId Id);
public record CountStrongByReadAggregate(KettleId Id);
public record CountStrongByReadModel(KettleId Id);

public static class LoneIdHandlers
{
    public static int LastCount;

    [AggregateHandler]
    public static KettleBoiled Handle(BoilByHandler command, Kettle kettle) => new();

    public static KettleBoiled Handle(BoilByWriteAggregate command, [WriteAggregate] Kettle kettle) => new();

    public static KettleBoiled Handle(BoilByWriteModel command, [WriteModel] Kettle kettle) => new();

    public static void Handle(CountByReadAggregate query, [ReadAggregate] Kettle kettle) => LastCount = kettle.Boils;

    public static void Handle(CountByReadModel query, [ReadModel] Kettle kettle) => LastCount = kettle.Boils;

    [AggregateHandler]
    public static KettleBoiled Handle(BoilStrongByHandler command, StrongKettle kettle) => new();

    public static KettleBoiled Handle(BoilStrongByWriteAggregate command, [WriteAggregate] StrongKettle kettle) => new();

    public static KettleBoiled Handle(BoilStrongByWriteModel command, [WriteModel] StrongKettle kettle) => new();

    public static void Handle(CountStrongByReadAggregate query, [ReadAggregate] StrongKettle kettle) => LastCount = kettle.Boils;

    public static void Handle(CountStrongByReadModel query, [ReadModel] StrongKettle kettle) => LastCount = kettle.Boils;
}

public class lone_id_member_binds_4883 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LoneIdHandlers));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "lone_id_4883";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> aKettle<T>() where T : class
    {
        var id = Guid.NewGuid();
        await using var session = theHost.DocumentStore().LightweightSession();
        session.Events.StartStream<T>(id, new KettleFilled(2));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<int> boilsOn<T>(Guid id) where T : class
    {
        await using var session = theHost.DocumentStore().QuerySession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        return events.Count(x => x.Data is KettleBoiled);
    }

    [Fact]
    public async Task a_lone_guid_id_binds_every_aggregate_attribute()
    {
        var id = await aKettle<Kettle>();

        await theHost.InvokeMessageAndWaitAsync(new BoilByHandler(id));
        await theHost.InvokeMessageAndWaitAsync(new BoilByWriteAggregate(id));
        await theHost.InvokeMessageAndWaitAsync(new BoilByWriteModel(id));
        (await boilsOn<Kettle>(id)).ShouldBe(3);

        await theHost.InvokeMessageAndWaitAsync(new CountByReadAggregate(id));
        LoneIdHandlers.LastCount.ShouldBe(3);
        LoneIdHandlers.LastCount = 0;
        await theHost.InvokeMessageAndWaitAsync(new CountByReadModel(id));
        LoneIdHandlers.LastCount.ShouldBe(3);
    }

    [Fact]
    public async Task a_lone_strong_typed_id_binds_every_aggregate_attribute()
    {
        var raw = await aKettle<StrongKettle>();
        var id = new KettleId(raw);

        await theHost.InvokeMessageAndWaitAsync(new BoilStrongByHandler(id));
        await theHost.InvokeMessageAndWaitAsync(new BoilStrongByWriteAggregate(id));
        await theHost.InvokeMessageAndWaitAsync(new BoilStrongByWriteModel(id));
        (await boilsOn<StrongKettle>(raw)).ShouldBe(3);

        await theHost.InvokeMessageAndWaitAsync(new CountStrongByReadAggregate(id));
        LoneIdHandlers.LastCount.ShouldBe(3);
        LoneIdHandlers.LastCount = 0;
        await theHost.InvokeMessageAndWaitAsync(new CountStrongByReadModel(id));
        LoneIdHandlers.LastCount.ShouldBe(3);
    }
}
