using Bobcat;
using Bobcat.Xunit;
using Marten;
using ProcessManagerViaHandlers.OrderFulfillment;
using ProcessManagerViaHandlers.OrderFulfillment.Handlers;
using Wolverine.Bobcat;
using Xunit;

namespace ProcessManagerViaHandlers.Tests.OrderFulfillment;

/// <summary>
/// The order fulfillment process manager as Bobcat specifications. Every step is a message, and every
/// handler appends conditionally — so most of what is worth specifying is when it appends NOTHING.
/// </summary>
[Collection("integration")]
public abstract class OrderFulfillmentSpec(AppFixture fixture) : WolverineSpec(fixture.Host!), IAsyncLifetime
{
    protected readonly Guid Id = Guid.NewGuid();
    protected readonly Guid Customer = Guid.NewGuid();

    protected OrderFulfillmentStarted Started => new(Id, Customer, 100m);

    public async ValueTask InitializeAsync() => await Host.ResetAllMartenDataAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[BobcatFeature("Starting order fulfillment"), BobcatScenario]
public class starting_order_fulfillment(AppFixture fixture) : OrderFulfillmentSpec(fixture)
{
    [Fact]
    public async Task the_process_starts_its_stream_and_a_payment_timer()
    {
        await GivenNoEventsFor<OrderFulfillmentState>(Id);

        await WhenReceived(new StartOrderFulfillment(Id, Customer, 100m));

        ThenEvents(new OrderFulfillmentStarted(Id, Customer, 100m));
        await ThenStreamIsStarted<OrderFulfillmentState>(Id);
        ThenMessageScheduled<PaymentTimeout>(StartOrderFulfillmentHandler.DefaultPaymentTimeoutWindow);
    }
}

[BobcatFeature("Continuing order fulfillment"), BobcatScenario]
public class continuing_order_fulfillment(AppFixture fixture) : OrderFulfillmentSpec(fixture)
{
    [Fact]
    public async Task a_step_is_recorded_while_others_are_outstanding()
    {
        await GivenEvents<OrderFulfillmentState>(Id, Started);

        await WhenReceived(new PaymentConfirmed(Id, 100m));

        ThenEvents(new PaymentConfirmed(Id, 100m));
    }

    [Fact]
    public async Task the_last_outstanding_step_completes_the_process()
    {
        await GivenEvents<OrderFulfillmentState>(Id, Started,
            new PaymentConfirmed(Id, 100m),
            new ItemsReserved(Id, Guid.NewGuid()));

        await WhenReceived(new ShipmentConfirmed(Id, "1Z999"));

        ThenEvents(new ShipmentConfirmed(Id, "1Z999"), new OrderFulfillmentCompleted(Id));

        var state = await TheAggregate<OrderFulfillmentState>(Id);
        Verify(state!, """
                       | PaymentConfirmed | ItemsReserved | ShipmentConfirmed | IsCompleted |
                       | true             | true          | true              | true        |
                       """);
    }

    [Fact]
    public async Task a_redelivered_step_is_ignored()
    {
        await GivenEvents<OrderFulfillmentState>(Id, Started, new PaymentConfirmed(Id, 100m));

        await WhenReceived(new PaymentConfirmed(Id, 100m));

        ThenNoEvents();
    }

    [Fact]
    public async Task a_late_step_after_cancellation_is_ignored()
    {
        await GivenEvents<OrderFulfillmentState>(Id, Started, new OrderFulfillmentCancelled(Id, "Customer changed their mind"));

        await WhenReceived(new ItemsReserved(Id, Guid.NewGuid()));

        ThenNoEvents();
    }
}

[BobcatFeature("Payment timeout"), BobcatScenario]
public class payment_timeout(AppFixture fixture) : OrderFulfillmentSpec(fixture)
{
    [Fact]
    public async Task an_unpaid_order_is_cancelled()
    {
        await GivenEvents<OrderFulfillmentState>(Id, Started);

        // The scheduled message itself, delivered now: no waiting on the scheduler
        await WhenReceived(new PaymentTimeout(Id));

        ThenEvents(new OrderFulfillmentCancelled(Id, "Payment timed out"));
    }

    [Fact]
    public async Task a_paid_order_ignores_the_timeout()
    {
        await GivenEvents<OrderFulfillmentState>(Id, Started, new PaymentConfirmed(Id, 100m));

        await WhenReceived(new PaymentTimeout(Id));

        ThenNoEvents();
    }
}

[BobcatFeature("Cancelling order fulfillment"), BobcatScenario]
public class cancelling_order_fulfillment(AppFixture fixture) : OrderFulfillmentSpec(fixture)
{
    [Fact]
    public async Task an_in_flight_process_is_cancelled()
    {
        await GivenEvents<OrderFulfillmentState>(Id, Started);

        await WhenReceived(new CancelOrderFulfillment(Id, "Out of stock"));

        ThenEvents(new OrderFulfillmentCancelled(Id, "Out of stock"));
    }

    [Fact]
    public async Task a_completed_process_cannot_be_cancelled()
    {
        await GivenEvents<OrderFulfillmentState>(Id, Started, new OrderFulfillmentCompleted(Id));

        await WhenReceived(new CancelOrderFulfillment(Id, "Too late"));

        ThenNoEvents();
    }
}
