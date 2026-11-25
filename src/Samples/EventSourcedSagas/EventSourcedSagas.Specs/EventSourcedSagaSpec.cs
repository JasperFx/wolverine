using Bobcat;
using Bobcat.CritterStack;
using EventSourcedSagas.Fulfillment;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Bobcat;

namespace EventSourcedSagas.Specs;

/// <summary>
/// The base of every specification here: acts in the fulfillment service, and treats a dead letter as an outcome to
/// judge rather than a failed act.
/// </summary>
public abstract class EventSourcedSagaSpec : WolverineSpec, IAsyncLifetime
{
    private readonly FulfillmentServices _services;

    protected EventSourcedSagaSpec(FulfillmentServices services) : base(services.Host)
    {
        _services = services;
        DeadLettersFailTheAct = false;

        // Three attempts and a compensation, each in a transaction of its own
        Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Every specification is about one order fulfillment.</summary>
    protected Guid OrderFulfillmentId { get; } = Guid.NewGuid();

    protected Guid CustomerId { get; } = Guid.NewGuid();

    public async ValueTask InitializeAsync() => await _services.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- the fulfillment's history, as events ----------------------------------------------------------------------

    protected OrderFulfillmentStarted Started(decimal totalAmount = 100m) => new(OrderFulfillmentId, CustomerId, totalAmount);

    protected static PaymentConfirmed Paid(decimal amount = 100m) => new(amount);

    protected static readonly Guid TheReservation = Guid.Parse("8d1f3c52-6a0e-4b8e-9c71-2f5a4d6e7b10");

    protected static ItemsReserved Reserved => new(TheReservation);

    protected static ShipmentConfirmed Shipped => new("1Z999AA10123456784");

    protected static Wolverine.SagaCompleted Completed => new();

    /// <summary>
    /// When the last event of the fulfillment's stream was recorded. A query, not a step. It returns only once the clock
    /// is clearly past that time, so an event appended after it can never carry the same timestamp: on a busy box the
    /// next append could land within the clock's resolution of this one, and a read "as of" this time then included it
    /// (GH-5958, seen on Fisher in a full LocalGate).
    /// </summary>
    protected async Task<DateTimeOffset> TheTimeOfTheLastEvent()
    {
        var events = await EventStores.FetchStreamAsync(Scenario.EventStore, OrderFulfillmentId);
        var timestamp = events[^1].Timestamp;

        var after = timestamp.AddMilliseconds(50);
        while (DateTimeOffset.UtcNow < after) await Task.Delay(10);

        return timestamp;
    }

    // ---- the fulfillment service's state ------------------------------------------------------------------------------

    [Given("the payment ledger is unavailable")]
    public void LedgerIsUnavailable() => _services.Host.Services.GetRequiredService<PaymentLedger>().Available = false;
}
