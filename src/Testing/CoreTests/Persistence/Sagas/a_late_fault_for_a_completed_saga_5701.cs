using CoreTests.Runtime.WorkerQueues;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// GH-5701: Wolverine doesn't guess which method completes a saga. Without [CompletesSaga], a catch-all Compensate covers
// what the completing method returns like anything else the saga sends. When that message fails after the saga is
// gone, its fault finds no saga and no Compensate runs.
//
// GH-5709: what happens to that fault. By default it is dead lettered tagged compensation-saga-missing on its first
// attempt, because retrying cannot bring the saga back. [SagaMissing(Discard)] on the saga discards it instead, and a
// NotFound(Fault<T>) method on the saga takes precedence over both
public class a_late_fault_for_a_completed_saga_5701
{
    private static async Task<IHost> hostFor<TSaga>(RecordingLoggerProvider? logs = null)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<TSaga>().IncludeType(typeof(GiftBoxCourier));

                opts.PublishFaultEvents();

                // Retries the fault would get if retrying could help
                opts.OnException<Exception>().RetryTimes(2);

                if (logs != null) opts.Services.AddSingleton<ILoggerProvider>(logs);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    // The saga completes and sends GiftBoxDelivered, which then fails for good, and its fault comes back to a saga
    // that is gone
    private static async Task<ITrackedSession> deliverAfterTheSagaCompleted(IHost host, Guid id)
    {
        await host.InvokeMessageAndWaitAsync(new StartGiftBox(id));

        return await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new DeliverGiftBox(id));
    }

    [Fact]
    public async Task a_fault_for_a_saga_that_is_gone_is_dead_lettered_without_retries()
    {
        using var host = await hostFor<GiftBoxSaga>();

        // The completing method isn't marked, so the catch-all covers its message
        host.GetRuntime().Options.HandlerGraph.SagaChains()
            .Where(x => x.SagaType == typeof(GiftBoxSaga))
            .Select(x => SagaCompensations.FaultMessageType(x.MessageType))
            .ShouldContain(typeof(GiftBoxDelivered));

        var id = Guid.NewGuid();
        var session = await deliverAfterTheSagaCompleted(host, id);

        host.Services.GetRequiredService<InMemorySagaPersistor>().Load<GiftBoxSaga>(id).ShouldBeNull();

        // Nothing is compensated
        GiftBoxSaga.Compensated.ShouldNotContain(id);
        session.Sent.MessagesOf<GiftBoxReturned>().ShouldBeEmpty();

        // The fault is dead lettered as a compensation whose saga was gone, on its first attempt
        var deadLettered = session.MovedToErrorQueue.RecordsInOrder()
            .Single(x => x.Envelope?.Message is Fault<GiftBoxDelivered>);
        deadLettered.Exception.ShouldBeOfType<UnknownSagaException>();
        deadLettered.Envelope!.Headers[DeadLetterQueueConstants.DeadLetterTagHeader]
            .ShouldBe(DeadLetterQueueConstants.CompensationSagaMissingTag);
        deadLettered.Envelope.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task a_saga_that_discards_messages_for_a_missing_saga_discards_its_faults_too()
    {
        var logs = new RecordingLoggerProvider();
        using var host = await hostFor<DiscardingGiftBoxSaga>(logs);

        var id = Guid.NewGuid();
        var session = await deliverAfterTheSagaCompleted(host, id);

        DiscardingGiftBoxSaga.Compensated.ShouldNotContain(id);
        session.Sent.MessagesOf<GiftBoxReturned>().ShouldBeEmpty();

        // Discarded: handled, not dead lettered, and logged at Information
        session.MovedToErrorQueue.RecordsInOrder().ShouldNotContain(x => x.Envelope!.Message is Fault<GiftBoxDelivered>);
        session.MessageSucceeded.MessagesOf<Fault<GiftBoxDelivered>>().ShouldHaveSingleItem();

        logs.Records.ShouldContain(x => x.Level == LogLevel.Information &&
                                        x.Message.Contains(typeof(DiscardingGiftBoxSaga).FullName!) &&
                                        x.Message.Contains("no longer exists"));
    }

    [Fact]
    public async Task a_not_found_method_for_the_fault_takes_precedence()
    {
        using var host = await hostFor<GiftBoxSagaWithNotFound>();

        var id = Guid.NewGuid();
        var session = await deliverAfterTheSagaCompleted(host, id);

        GiftBoxSagaWithNotFound.Compensated.ShouldNotContain(id);
        GiftBoxSagaWithNotFound.NotFoundFaults.ShouldContain(id);

        session.MovedToErrorQueue.RecordsInOrder().ShouldNotContain(x => x.Envelope!.Message is Fault<GiftBoxDelivered>);
        session.MessageSucceeded.MessagesOf<Fault<GiftBoxDelivered>>().ShouldHaveSingleItem();
    }
}

public record StartGiftBox(Guid Id);

public record DeliverGiftBox(Guid Id);

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

// The same saga, but a message for it once it is gone, its faults included, is discarded
[WolverineIgnore]
[SagaMissing(SagaMissingBehavior.Discard)]
public class DiscardingGiftBoxSaga : CompensatingSaga
{
    public static readonly HashSet<Guid> Compensated = new();

    public Guid Id { get; set; }

    public static DiscardingGiftBoxSaga Start(StartGiftBox command) => new() { Id = command.Id };

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

// The same saga, with its own say on a fault that arrives after it is gone
[WolverineIgnore]
public class GiftBoxSagaWithNotFound : CompensatingSaga
{
    public static readonly HashSet<Guid> Compensated = new();
    public static readonly HashSet<Guid> NotFoundFaults = new();

    public Guid Id { get; set; }

    public static GiftBoxSagaWithNotFound Start(StartGiftBox command) => new() { Id = command.Id };

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

    public static void NotFound(Fault<GiftBoxDelivered> fault)
    {
        lock (NotFoundFaults) NotFoundFaults.Add(fault.Message.Id);
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
