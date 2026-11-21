using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// GH-5701: Wolverine doesn't guess which method completes a saga. Without [CompletesSaga], a catch-all Compensate covers
// what the completing method returns like anything else the saga sends. When that message fails after the saga is
// gone, its fault takes the usual path for a compensation with no saga: no Compensate runs, and the fault is dead
// lettered tagged compensation-saga-missing
public class a_late_fault_for_a_completed_saga_5701
{
    [Fact]
    public async Task a_fault_from_an_unmarked_completing_method_after_the_saga_completed_is_not_compensated()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<GiftBoxSaga>().IncludeType(typeof(GiftBoxCourier));

                opts.PublishFaultEvents();
                opts.OnException<Exception>().RetryTimes(2);
            }).StartAsync(TestContext.Current.CancellationToken);

        // The completing method isn't marked, so the catch-all covers its message
        host.GetRuntime().Options.HandlerGraph.SagaChains()
            .Where(x => x.SagaType == typeof(GiftBoxSaga))
            .Select(x => SagaCompensations.FaultMessageType(x.MessageType))
            .ShouldContain(typeof(GiftBoxDelivered));

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartGiftBox(id));

        // The saga completes and sends GiftBoxDelivered, which then fails for good
        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .WaitForCondition(new DeadLettered<Fault<GiftBoxDelivered>>())
            .SendMessageAndWaitAsync(new DeliverGiftBox(id));

        host.Services.GetRequiredService<InMemorySagaPersistor>().Load<GiftBoxSaga>(id).ShouldBeNull();

        // The fault finds no saga: nothing is compensated, and the fault is dead lettered as a compensation whose saga
        // was gone
        GiftBoxSaga.Compensated.ShouldNotContain(id);
        session.Sent.MessagesOf<GiftBoxReturned>().ShouldBeEmpty();

        var deadLettered = session.MovedToErrorQueue.RecordsInOrder()
            .Single(x => x.Envelope?.Message is Fault<GiftBoxDelivered>);
        deadLettered.Exception.ShouldBeOfType<UnknownSagaException>();
        deadLettered.Envelope!.Headers[DeadLetterQueueConstants.DeadLetterTagHeader]
            .ShouldBe(DeadLetterQueueConstants.CompensationSagaMissingTag);
    }
}

public record StartGiftBox(Guid Id);

public record DeliverGiftBox(Guid GiftBoxSagaId);

public record GiftBoxDelivered(Guid Id);

public record GiftBoxReturned(Guid Id);

public class GiftBoxLostException() : Exception("The gift box was lost");

// Only in the host this test builds: CoreTests' shared hosts scan this assembly
[WolverineIgnore]
public class GiftBoxSaga : CompensatingSaga
{
    public static readonly HashSet<Guid> Compensated = new();

    public Guid Id { get; set; }

    public static GiftBoxSaga Start(StartGiftBox command) => new() { Id = command.Id };

    // Completes the saga, but isn't marked with [CompletesSaga]
    public GiftBoxDelivered Handle(DeliverGiftBox command)
    {
        MarkCompleted();
        return new GiftBoxDelivered(Id);
    }

    public GiftBoxReturned Compensate(object failed, ExceptionInfo ex)
    {
        lock (Compensated) Compensated.Add(Id);
        return new GiftBoxReturned(Id);
    }
}

[WolverineIgnore]
public static class GiftBoxCourier
{
    public static void Handle(GiftBoxDelivered delivered) => throw new GiftBoxLostException();

    public static void Handle(GiftBoxReturned returned)
    {
    }
}
