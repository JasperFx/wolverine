using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

/// <summary>
/// Reproducer for https://github.com/JasperFx/wolverine/issues/2595.
///
/// When a saga's static <c>Start</c> method generates its own saga id inside
/// the method body and cascades a message tagged with an explicit
/// <c>DeliveryOptions { SagaId = ... }</c>, the explicit value should win over
/// the inbound envelope's <c>SagaId</c>. Before the fix at
/// <c>MessageContext.TrackEnvelopeCorrelation</c>, the inbound envelope's
/// <c>SagaId</c> (or the context's <c>_sagaId</c>) silently overwrote the
/// explicit value, so a downstream reply that auto-propagates
/// <c>envelope.SagaId</c> would route back to the wrong saga.
/// </summary>
public class Bug_2595_explicit_delivery_options_sagaid_should_win : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<Bug2595ChildSaga>()
                    .IncludeType<Bug2595WorkHandler>();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task explicit_delivery_options_sagaid_on_saga_start_cascade_should_win()
    {
        // Simulate a parent saga context: send StartChild with an explicit
        // envelope.SagaId representing some unrelated parent saga's id. The
        // ChildSaga.Start method will see this on its inbound envelope.
        var parentSagaId = Guid.NewGuid().ToString();

        var tracked = await _host.TrackActivity()
            .Timeout(15.Seconds())
            .SendMessageAndWaitAsync(new Bug2595StartChild(),
                new DeliveryOptions { SagaId = parentSagaId });

        // The DoWork envelope cascaded out of ChildSaga.Start should carry the
        // explicit DeliveryOptions.SagaId set by Start (the new ChildSaga.Id),
        // not the inbound envelope's SagaId.
        var doWorkEnvelope = tracked.Sent.Envelopes()
            .Single(e => e.Message is Bug2595DoWork);

        doWorkEnvelope.SagaId.ShouldNotBeNullOrEmpty();
        doWorkEnvelope.SagaId.ShouldNotBe(parentSagaId,
            "Saga Start cascades should preserve the explicit DeliveryOptions.SagaId set " +
            "by the saga's Start method, not be overridden by the inbound envelope's SagaId.");

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        // Load the specific child saga that this run produced (the test does
        // not own its document table; load by id rather than asserting total
        // table count, so concurrent or prior runs don't break the assertion).
        var childId = Guid.Parse(doWorkEnvelope.SagaId!);
        var child = await session.LoadAsync<Bug2595ChildSaga>(childId);
        child.ShouldNotBeNull("ChildSaga.Start must have inserted the saga document");

        // Final proof: the WorkDone reply auto-propagated the (correct) child
        // saga id, ChildSaga.Handle(WorkDone) ran, and recorded WorkDone=true
        // on the child saga document. If the bug were still present the reply
        // would carry parentSagaId and ChildSaga.Handle(WorkDone) would fail
        // with UnknownSagaException.
        child.WorkDone.ShouldBeTrue(
            "ChildSaga.Handle(WorkDone) should run, proving the explicit SagaId round-tripped.");
    }
}

public record Bug2595StartChild;

// Cascaded by ChildSaga.Start with an explicit DeliveryOptions { SagaId = childSagaId }.
// A plain handler responds with WorkDone; that reply auto-propagates the inbound
// envelope.SagaId.
public record Bug2595DoWork(Guid Sentinel);

public record Bug2595WorkDone(Guid Sentinel);

public class Bug2595ChildSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public Guid Sentinel { get; set; } = Guid.NewGuid();
    public bool WorkDone { get; set; }

    public static (Bug2595ChildSaga, OutgoingMessages) Start(Bug2595StartChild _)
    {
        var childId = Guid.NewGuid();
        var sentinel = Guid.NewGuid();

        var outgoing = new OutgoingMessages
        {
            // Explicit DeliveryOptions.SagaId — should target the new ChildSaga,
            // *not* the inbound envelope's SagaId.
            { new Bug2595DoWork(sentinel), new DeliveryOptions { SagaId = childId.ToString() } }
        };

        return (new Bug2595ChildSaga { Id = childId, Sentinel = sentinel }, outgoing);
    }

    public void Handle(Bug2595WorkDone _) => WorkDone = true;
}

// Plain (non-saga) handler standing in for an external service. Its reply
// auto-propagates the inbound envelope.SagaId.
public class Bug2595WorkHandler
{
    public static Bug2595WorkDone Handle(Bug2595DoWork message) => new(message.Sentinel);
}
