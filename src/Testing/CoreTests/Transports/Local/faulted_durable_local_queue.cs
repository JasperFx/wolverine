using System.Reflection;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Transports.Local;

/// <summary>
/// GH-5358. The durable twin of <see cref="faulted_buffered_local_queue" />: no ListeningAgent owns a durable local
/// queue either, so a receiver whose block faulted terminally (jasperfx#506) was never replaced, and every message sent
/// to the queue afterwards threw until the process restarted.
/// </summary>
public class faulted_durable_local_queue : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(faulted_durable_local_queue).Assembly;
            opts.Services.AddSingleton<IMessageStore>(new NullMessageStore());
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FaultedLaneHandler));

            opts.PublishMessage<FaultedLaneMessage>().ToLocalQueue("faulted-durable-lane").UseDurableInbox();
        }).StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_message_sent_after_the_fault_is_still_handled()
    {
        var queue = localQueue();
        var faulted = receiverOf(queue);
        faultTerminally(faulted);
        faulted.HasFaulted.ShouldBeTrue();

        var session = await _host.TrackActivity().Timeout(10.Seconds())
            .PublishMessageAndWaitAsync(new FaultedLaneMessage());

        session.Executed.SingleMessage<FaultedLaneMessage>().ShouldNotBeNull();

        var rebuilt = receiverOf(queue);
        rebuilt.ShouldNotBeSameAs(faulted);
        rebuilt.HasFaulted.ShouldBeFalse();
        ((IListenerCircuit)queue).Status.ShouldBe(ListeningStatus.Accepting);
    }

    [Fact]
    public async Task the_queue_keeps_working_after_it_was_rebuilt()
    {
        var queue = localQueue();
        faultTerminally(receiverOf(queue));

        await _host.TrackActivity().Timeout(10.Seconds()).PublishMessageAndWaitAsync(new FaultedLaneMessage());
        var rebuilt = receiverOf(queue);

        var session = await _host.TrackActivity().Timeout(10.Seconds())
            .PublishMessageAndWaitAsync(new FaultedLaneMessage());

        session.Executed.SingleMessage<FaultedLaneMessage>().ShouldNotBeNull();
        receiverOf(queue).ShouldBeSameAs(rebuilt);
    }

    private DurableLocalQueue localQueue()
    {
        return theRuntime.Endpoints.AgentForLocalQueue("faulted-durable-lane").ShouldBeOfType<DurableLocalQueue>();
    }

    private static DurableReceiver receiverOf(DurableLocalQueue queue)
    {
        return (DurableReceiver)typeof(DurableLocalQueue)
            .GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(queue)!;
    }

    // Fault the real block the way its consumer loop does when an exception escapes it, e.g. an
    // OutOfMemoryException thrown from WaitToReadAsync
    private static void faultTerminally(DurableReceiver receiver)
    {
        var block = (Block<Envelope>)typeof(DurableReceiver)
            .GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(receiver)!;

        typeof(Block<Envelope>)
            .GetMethod("fault", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(block, [new OutOfMemoryException()]);

        block.Failure.ShouldNotBeNull();
    }
}
