using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

// GH-3025: a handler returning a SINGLE IMartenOp (e.g. MartenOps.StartStream / MartenOps.Store)
// must persist even without opts.Policies.AutoApplyTransactions(). Previously MartenOpPolicy only
// applied Marten transaction support (the SaveChangesAsync postprocessor) for IEnumerable<IMartenOp>
// returns, so a single op was Execute()'d onto the session and then silently dropped.
public class single_marten_op_side_effect_persists : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "single_op_3025";
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                // Deliberately NO opts.Policies.AutoApplyTransactions().
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StartViaOpHandler))
                    .IncludeType(typeof(StoreViaOpHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task single_start_stream_op_persists_without_auto_transactions()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new StartViaOp(id));

        await using var session = theStore.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);
        events.Count.ShouldBe(1); // was 0 (op dropped) before GH-3025
    }

    [Fact]
    public async Task single_store_op_persists_without_auto_transactions()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new StoreViaOp(id));

        await using var session = theStore.LightweightSession();
        (await session.LoadAsync<OpDoc>(id)).ShouldNotBeNull();
    }
}

public record StartViaOp(Guid Id);

public record OpStarted(string Name);

public class OpTally
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public void Apply(OpStarted e) => Name = e.Name;
}

public static class StartViaOpHandler
{
    public static IMartenOp Handle(StartViaOp command)
        => MartenOps.StartStream<OpTally>(command.Id, new OpStarted("created"));
}

public record StoreViaOp(Guid Id);

public class OpDoc
{
    public Guid Id { get; set; }
}

public static class StoreViaOpHandler
{
    public static IMartenOp Handle(StoreViaOp command)
        => MartenOps.Store(new OpDoc { Id = command.Id });
}
