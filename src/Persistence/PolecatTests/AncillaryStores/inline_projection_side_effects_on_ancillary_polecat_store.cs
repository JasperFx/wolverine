using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Projections;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

// GH-3109 acceptance criterion: inline-projection side effects (RaiseSideEffects → PublishMessage) on
// an ANCILLARY Polecat store must relay through the Wolverine outbox. The ancillary store's
// PolecatToWolverineOutbox bridge (wired by PolecatOverrides<T>) is what makes this work — without it
// the published message would hit Polecat's NulloMessageOutbox and be dropped.
public class inline_projection_side_effects_on_ancillary_polecat_store : IAsyncLifetime
{
    private IHost theHost = null!;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "se_main";
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                // Ancillary store with an INLINE projection that publishes a Wolverine message from
                // RaiseSideEffects. EnableSideEffectsOnInlineProjections is the per-store opt-in.
                opts.Services.AddPolecatStore<ISideEffectStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "se_ancillary";
                        m.Events.EnableSideEffectsOnInlineProjections = true;
                        m.Projections.Add(new CounterSideEffectProjection(), ProjectionLifecycle.Inline);
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CounterIncrementedHandler));
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task inline_projection_side_effect_relays_through_the_wolverine_outbox()
    {
        CounterIncrementedHandler.Seen.Clear();
        var streamId = Guid.NewGuid();

        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                using var scope = theHost.Services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<ISideEffectStore>();
                await using var session = store.LightweightSession();
                session.Events.StartStream<Counter>(streamId, new IncrementCounter());
                await session.SaveChangesAsync();
            }));

        tracked.Executed.MessagesOf<CounterIncremented>()
            .ShouldContain(m => m.StreamId == streamId);
    }
}

public interface ISideEffectStore : IDocumentStore;

public record IncrementCounter;

public record CounterIncremented(Guid StreamId, int Count);

public class Counter
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(IncrementCounter _) => Count++;
}

public class CounterSideEffectProjection : SingleStreamProjection<Counter, Guid>
{
    public static Counter Create(IncrementCounter _, IEvent metadata) =>
        new() { Id = metadata.StreamId, Count = 1 };

    public override ValueTask RaiseSideEffects(IDocumentSession session, IEventSlice<Counter> slice)
    {
        if (slice.Snapshot is not null)
        {
            slice.PublishMessage(new CounterIncremented(slice.Snapshot.Id, slice.Snapshot.Count));
        }

        return ValueTask.CompletedTask;
    }
}

public static class CounterIncrementedHandler
{
    public static readonly List<Guid> Seen = new();

    public static void Handle(CounterIncremented message)
    {
        lock (Seen) Seen.Add(message.StreamId);
    }
}
