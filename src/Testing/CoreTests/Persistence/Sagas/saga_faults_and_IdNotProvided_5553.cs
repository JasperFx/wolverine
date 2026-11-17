using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// GH-5553 and GH-4484: the plain xUnit side of what the SagaFaults Bobcat specifications show end to end
public class saga_faults_and_IdNotProvided_5553
{
    [Fact]
    public async Task a_saga_hears_the_fault_of_a_message_it_sent_to_another_service_with_fault_events_off()
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

        // No PublishFaultEvents() and no route for Fault<ReserveStock>: the saga asked for the fault itself
        using var stockService = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "stock-service";
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ReserveStockHandler>();
                opts.ListenAtPort(stockPort);
                opts.OnException<StockUnavailableException>().RetryTimes(2);
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();

        var session = await sagaService.TrackActivity()
            .AlsoTrack(stockService)
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<Fault<ReserveStock>>(sagaService)
            .InvokeMessageAndWaitAsync(new StartFaultAwareShipping(id));

        // The saga's ReserveStock asked for its fault at the saga service's own address
        var sent = session.Sent.Envelopes().First(x => x.Message is ReserveStock);
        sent.Headers[FaultHeaders.ReplyTo].ShouldBe($"tcp://localhost:{sagaPort}/");

        var received = session.Received.Envelopes().Single(x => x.Message is Fault<ReserveStock>);
        received.SagaId.ShouldBe(id.ToString());

        await Poll(() => sagaService.Services.GetRequiredService<InMemorySagaPersistor>()
            .Load<FaultAwareShippingSaga>(id)?.ReservationFailed == true);
    }

    [Fact]
    public async Task with_fault_events_on_in_one_service_the_saga_hears_the_fault_once()
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

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .InvokeMessageAndWaitAsync(new StartFaultAwareShipping(Guid.NewGuid()));

        // Routed locally and asked for at the reply address, which is also local: one fault, not two
        session.AutoFaultsPublished.Envelopes().Count(x => x.Message is Fault<ReserveStock>).ShouldBe(1);
    }

    [Fact]
    public async Task a_message_with_no_saga_id_goes_to_IdNotProvided()
    {
        using var host = await sagaHost(typeof(UnmatchedSaga), typeof(NamedUnmatchedSaga));

        // No saga id member, no saga-id header
        var session = await host.InvokeMessageAndWaitAsync(new SagaNudge("from nowhere"));
        session.Sent.SingleMessage<UnmatchedNudge>().Note.ShouldBe("from nowhere");

        // A saga id member left at its default
        session = await host.InvokeMessageAndWaitAsync(new SagaPoke(Guid.Empty));
        session.Sent.SingleMessage<UnmatchedPoke>();

        // A string saga id member left empty
        session = await host.InvokeMessageAndWaitAsync(new NamedSagaPoke(""));
        session.Sent.SingleMessage<UnmatchedPoke>();
    }

    [Fact]
    public async Task a_message_with_a_saga_id_still_reaches_its_saga()
    {
        using var host = await sagaHost(typeof(UnmatchedSaga));
        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartUnmatchedSaga(id));

        await host.TrackActivity().SendMessageAndWaitAsync(new SagaNudge("hello"), new DeliveryOptions { SagaId = id.ToString() });
        await host.InvokeMessageAndWaitAsync(new SagaPoke(id));

        var saga = host.Services.GetRequiredService<InMemorySagaPersistor>().Load<UnmatchedSaga>(id);
        saga.ShouldNotBeNull();
        saga.Nudges.ShouldBe(1);
        saga.Pokes.ShouldBe(1);
    }

    [Fact]
    public async Task IdNotProvided_must_be_static()
    {
        using var host = await sagaHost(typeof(InstanceIdNotProvidedSaga));

        var ex = await Should.ThrowAsync<Exception>(() => host.InvokeMessageAndWaitAsync(new InstanceNudge("x")));
        ex.ToString().ShouldContain("must be static");
    }

    [Fact]
    public async Task IdNotProvided_is_only_a_handler_method_on_a_saga()
    {
        using var host = await sagaHost(typeof(UnmatchedSaga), typeof(NotASaga));

        var graph = host.Services.GetRequiredService<IWolverineRuntime>().Options.HandlerGraph;
        graph.ChainFor(typeof(LonelyMessage)).ShouldBeNull();
        graph.ChainFor(typeof(SagaNudge)).ShouldNotBeNull();
    }

    [Fact]
    public async Task a_saga_that_only_declares_NotFound_for_a_message_does_not_create_a_saga_for_it()
    {
        using var host = await sagaHost(typeof(UnmatchedSaga));
        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartUnmatchedSaga(id));

        // The saga exists: nothing to do, and certainly no new, empty saga
        await host.InvokeMessageAndWaitAsync(new SagaCheck(id));
        host.Services.GetRequiredService<InMemorySagaPersistor>().Load<UnmatchedSaga>(Guid.Empty).ShouldBeNull();

        // The saga is gone: NotFound
        var session = await host.InvokeMessageAndWaitAsync(new SagaCheck(Guid.NewGuid()));
        session.Sent.SingleMessage<SagaWasGone>();
    }

    [Fact]
    public async Task inferred_grouping_reads_the_saga_id_header_for_a_message_with_no_saga_id_member()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FaultAwareShippingSaga>()
                    .IncludeType<UnmatchedSaga>();
                opts.MessagePartitioning.UseInferredMessageGrouping();
            }).StartAsync(TestContext.Current.CancellationToken);

        var partitioning = host.Services.GetRequiredService<IWolverineRuntime>().Options.MessagePartitioning;

        partitioning.GroupsBySagaId(typeof(Fault<ReserveStock>)).ShouldBeTrue();
        partitioning.GroupsBySagaId(typeof(SagaNudge)).ShouldBeTrue();

        // A saga id member of its own decides the group the way it always has
        partitioning.GroupsBySagaId(typeof(SagaPoke)).ShouldBeFalse();
        partitioning.GroupsBySagaId(typeof(ReserveStock)).ShouldBeFalse();

        var envelope = new Envelope(new SagaNudge("x")) { SagaId = "saga-1" };
        partitioning.DetermineGroupId(envelope).ShouldBe("saga-1");
        partitioning.DetermineGroupId(new Envelope(new SagaNudge("x"))).ShouldBeNull();
    }

    private static Task<IHost> sagaHost(params Type[] sagaTypes)
        => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(UnmatchedMessages));
                foreach (var sagaType in sagaTypes) discovery.IncludeType(sagaType);
            }).StartAsync(TestContext.Current.CancellationToken);

    private static async Task Poll(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!condition() && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue();
    }
}

public record StartUnmatchedSaga(Guid Id);

// No saga id member: the saga-id header, or nothing
public record SagaNudge(string Note);

public record SagaPoke(Guid UnmatchedSagaId);

public record NamedSagaPoke([property: SagaIdentity] string Name);

public record SagaCheck(Guid UnmatchedSagaId);

public record UnmatchedNudge(string Note);

public record UnmatchedPoke;

public record SagaWasGone;

public record InstanceNudge(string Note);

#region sample_saga_IdNotProvided
public class UnmatchedSaga : Saga
{
    public Guid Id { get; set; }
    public int Nudges { get; set; }
    public int Pokes { get; set; }

    public static UnmatchedSaga Start(StartUnmatchedSaga command) => new() { Id = command.Id };

    public void Handle(SagaNudge nudge) => Nudges++;

    public void Handle(SagaPoke poke) => Pokes++;

    // A SagaNudge that reached this saga with no saga id, instead of IndeterminateSagaStateIdException
    public static UnmatchedNudge IdNotProvided(SagaNudge nudge) => new(nudge.Note);

    public static UnmatchedPoke IdNotProvided(SagaPoke poke) => new();

    // Only NotFound for SagaCheck: an existing saga has nothing to do with it
    public static SagaWasGone NotFound(SagaCheck check) => new();
}
#endregion

// A string saga id: the other branch of the generated id check
public class NamedUnmatchedSaga : Saga
{
    public string Id { get; set; } = "";

    public void Handle(NamedSagaPoke poke)
    {
    }

    public static UnmatchedPoke IdNotProvided(NamedSagaPoke poke) => new();
}

public class InstanceIdNotProvidedSaga : Saga
{
    public Guid Id { get; set; }

    public void Handle(InstanceNudge nudge)
    {
    }

    public void IdNotProvided(InstanceNudge nudge)
    {
    }
}

public record LonelyMessage;

public class NotASaga
{
    public static void IdNotProvided(LonelyMessage message)
    {
    }
}

public static class UnmatchedMessages
{
    public static void Handle(UnmatchedNudge message)
    {
    }

    public static void Handle(UnmatchedPoke message)
    {
    }

    public static void Handle(SagaWasGone message)
    {
    }
}
