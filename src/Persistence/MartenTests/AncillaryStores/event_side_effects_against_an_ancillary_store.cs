using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

/// <summary>
///     Storage.AppendEvents() / Storage.StartStream() have to reach an <b>ancillary</b> event store, not just
///     the application's primary one. This is the case that matters for CritterWatch, which registers its own
///     store rather than the host application's.
/// </summary>
/// <remarks>
///     The mechanism is worth stating because it is why this works with no extra plumbing: the shared
///     IEventOperations variable source resolves whichever <c>IDocumentSession</c> the chain has, and
///     <c>[Storage(typeof(IEventSideEffectStore))]</c> has already swapped that session for the ancillary
///     store's outbox-enrolled one at the front of the chain's middleware. Asserting the negative -- that the
///     events did NOT land in the main store -- is the point; a side effect that silently fell back to the
///     primary store would still satisfy a positive-only assertion against the ancillary one.
/// </remarks>
public class event_side_effects_against_an_ancillary_store : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "evt_side_effect_main";
                    m.Events.DatabaseSchemaName = "evt_side_effect_main";
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<IEventSideEffectStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "evt_side_effect_ancillary";
                    m.Events.DatabaseSchemaName = "evt_side_effect_ancillary";
                }).IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AncillaryInvoiceHandler));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task start_stream_lands_in_the_targeted_store_and_not_the_main_one()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new CreateAncillaryInvoice(id, 250));

        // ...landed in the ancillary store
        var ancillary = theHost.Services.GetRequiredService<IEventSideEffectStore>();
        await using (var session = ancillary.LightweightSession())
        {
            var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
            events.Count.ShouldBe(1);
            events[0].Data.ShouldBeOfType<AncillaryInvoiceCreated>().Amount.ShouldBe(250);
        }

        // ...and demonstrably NOT in the main store
        await using (var main = theHost.DocumentStore().LightweightSession())
        {
            var events = await main.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
            events.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task append_events_also_routes_to_the_targeted_store()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new CreateAncillaryInvoice(id, 250));

        await theHost.InvokeMessageAndWaitAsync(new ApproveAncillaryInvoice(id, "petrovic"));

        var ancillary = theHost.Services.GetRequiredService<IEventSideEffectStore>();
        await using var session = ancillary.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(2);
        events[1].Data.ShouldBeOfType<AncillaryInvoiceApproved>().ApprovedBy.ShouldBe("petrovic");
    }
}

public interface IEventSideEffectStore : IDocumentStore;

public record AncillaryInvoiceCreated(decimal Amount);

public record AncillaryInvoiceApproved(string ApprovedBy);

public record CreateAncillaryInvoice(Guid Id, decimal Amount);

public record ApproveAncillaryInvoice(Guid Id, string ApprovedBy);

public static class AncillaryInvoiceHandler
{
    [Storage(typeof(IEventSideEffectStore))]
    public static StartStream Handle(CreateAncillaryInvoice command)
        => Storage.StartStream(command.Id, new AncillaryInvoiceCreated(command.Amount));

    [Storage(typeof(IEventSideEffectStore))]
    public static AppendEvents Handle(ApproveAncillaryInvoice command)
        => Storage.AppendEvents(command.Id, new AncillaryInvoiceApproved(command.ApprovedBy));
}
