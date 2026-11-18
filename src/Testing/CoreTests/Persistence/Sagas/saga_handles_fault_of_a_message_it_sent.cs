using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// GH-5553 phase 0: prove what already works today when a saga declares Handle(Fault<X>) for a message X
// it sent. Fault<T> has no Id member, so SagaChain falls back to PullSagaIdFromEnvelopeFrame and the
// fault correlates to the saga by the saga-id envelope header that MessageContext copies from the failing
// envelope onto the fault envelope. No production changes; these tests record the current behavior.
public class saga_handles_fault_of_a_message_it_sent
{
    [Fact]
    public async Task one_service_saga_handles_the_fault_of_a_message_it_sent()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FaultAwareShippingSaga>()
                    .IncludeType<ReserveStockHandler>();

                opts.PublishFaultEvents();

                // Retry twice, then the failure moves the message to the dead letter queue,
                // which is what publishes the Fault<ReserveStock>
                opts.OnException<StockUnavailableException>().RetryTimes(2);
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .InvokeMessageAndWaitAsync(new StartFaultAwareShipping(id));

        // The X handler really failed, after its retries
        session.AllExceptions().OfType<StockUnavailableException>().ShouldNotBeEmpty();

        // The fault was published with the saga id on the envelope...
        var fault = session.AutoFaultsPublished.Envelopes()
            .Single(x => x.Message is Fault<ReserveStock>);
        fault.SagaId.ShouldBe(id.ToString());

        // ...and the saga's Handle(Fault<ReserveStock>) ran against the saga that sent it
        var saga = host.Services.GetRequiredService<InMemorySagaPersistor>().Load<FaultAwareShippingSaga>(id);
        saga.ShouldNotBeNull();
        saga.ReservationFailed.ShouldBeTrue();
        saga.FailureExceptionType.ShouldBe(typeof(StockUnavailableException).FullName);
        saga.FailedSku.ShouldBe("sku-" + id);
    }

    [Fact]
    public async Task two_services_with_an_explicit_fault_routing_rule_back_to_the_saga()
    {
        var sagaPort = PortFinder.GetAvailablePort();
        var stockPort = PortFinder.GetAvailablePort();

        using var sagaService = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "saga-service";
                opts.Discovery.DisableConventionalDiscovery().IncludeType<FaultAwareShippingSaga>();
                opts.ListenAtPort(sagaPort);
                opts.PublishMessage<ReserveStock>().ToPort(stockPort);
            }).StartAsync(TestContext.Current.CancellationToken);

        using var stockService = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "stock-service";
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ReserveStockHandler>();
                opts.ListenAtPort(stockPort);

                opts.PublishFaultEvents();
                opts.OnException<StockUnavailableException>().RetryTimes(2);

                #region sample_route_fault_back_to_the_saga_service
                // Fault<T> is published through routing only, so the service that fails X needs an
                // explicit rule to send Fault<X> back to the service where the saga lives
                opts.PublishMessage<Fault<ReserveStock>>().ToPort(sagaPort);
                #endregion
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();

        var session = await sagaService.TrackActivity()
            .AlsoTrack(stockService)
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<Fault<ReserveStock>>(sagaService)
            .InvokeMessageAndWaitAsync(new StartFaultAwareShipping(id));

        session.AllExceptions().OfType<StockUnavailableException>().ShouldNotBeEmpty();

        var received = session.Received.Envelopes().Single(x => x.Message is Fault<ReserveStock>);
        received.SagaId.ShouldBe(id.ToString());

        // Wait for the saga service to finish handling the fault it received
        await Poll(() => sagaService.Services.GetRequiredService<InMemorySagaPersistor>()
            .Load<FaultAwareShippingSaga>(id)?.ReservationFailed == true);

        var saga = sagaService.Services.GetRequiredService<InMemorySagaPersistor>()
            .Load<FaultAwareShippingSaga>(id);
        saga.ShouldNotBeNull();
        saga.ReservationFailed.ShouldBeTrue();
        saga.FailureExceptionType.ShouldBe(typeof(StockUnavailableException).FullName);
    }

    [Fact]
    public async Task a_fault_with_no_saga_id_cannot_find_its_saga_today()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FaultAwareShippingSaga>()
                    .IncludeType<ReserveStockHandler>();

                opts.PublishFaultEvents();
                opts.OnException<StockUnavailableException>().RetryTimes(2);
            }).StartAsync(TestContext.Current.CancellationToken);

        // ReserveStock sent by something that is not the saga, so neither it nor its fault has a saga id
        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .WaitForCondition(new FaultDeadLettered())
            .PublishMessageAndWaitAsync(new ReserveStock("sku-orphan"));

        var fault = session.AutoFaultsPublished.Envelopes()
            .Single(x => x.Message is Fault<ReserveStock>);
        fault.SagaId.ShouldBeNull();

        // A saga with no IdNotProvided for Fault<ReserveStock> still throws IndeterminateSagaStateIdException, and the
        // fault itself is dead-lettered. GH-4484's IdNotProvided hook is opt-in: see saga_faults_and_IdNotProvided_5553
        var deadLettered = session.MovedToErrorQueue.Envelopes().Single(x => x.Message is Fault<ReserveStock>);
        var record = session.AllRecordsInOrder(MessageEventType.MovedToErrorQueue)
            .Single(x => x.Envelope?.Id == deadLettered.Id);
        record.Exception.ShouldBeOfType<IndeterminateSagaStateIdException>();
    }

    private static async Task Poll(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!condition() && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}

public record StartFaultAwareShipping(Guid Id);

// Deliberately no Id / SagaId member: the fault can only correlate by the envelope's saga-id header
public record ReserveStock(string Sku);

public class StockUnavailableException(string sku) : Exception($"No stock for {sku}");

public class ReserveStockHandler
{
    public static void Handle(ReserveStock message) => throw new StockUnavailableException(message.Sku);
}

#region sample_saga_handling_the_fault_of_a_message_it_sent
public class FaultAwareShippingSaga : Saga
{
    public Guid Id { get; set; }
    public bool ReservationFailed { get; set; }
    public string? FailureExceptionType { get; set; }
    public string? FailedSku { get; set; }

    public static (FaultAwareShippingSaga, ReserveStock) Start(StartFaultAwareShipping command)
    {
        return (new FaultAwareShippingSaga { Id = command.Id }, new ReserveStock("sku-" + command.Id));
    }

    // Fault<ReserveStock> has no saga id member, so Wolverine reads the saga id
    // from the envelope that carried the fault
    public void Handle(Fault<ReserveStock> fault)
    {
        ReservationFailed = true;
        FailureExceptionType = fault.Exception.Type;
        FailedSku = fault.Message.Sku;
    }
}
#endregion

internal class FaultDeadLettered : ITrackedCondition
{
    private bool _completed;

    public void Record(EnvelopeRecord record)
    {
        if (record.MessageEventType == MessageEventType.MovedToErrorQueue &&
            record.Envelope?.Message is Fault<ReserveStock>)
        {
            _completed = true;
        }
    }

    public bool IsCompleted() => _completed;
}
