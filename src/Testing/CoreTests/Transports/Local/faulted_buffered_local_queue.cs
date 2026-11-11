using System.Reflection;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Transports.Local;

/// <summary>
/// A buffered local queue has no ListeningAgent to rebuild it, so a terminal block fault (jasperfx#506) used to
/// leave it rejecting every message until the process restarted. On the "agents" queue that meant a node kept
/// heartbeating while it could no longer run a single agent command.
/// </summary>
public class faulted_buffered_local_queue : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(faulted_buffered_local_queue).Assembly;
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FaultedLaneHandler));

            opts.PublishMessage<FaultedLaneMessage>().ToLocalQueue("faulted-lane").BufferedInMemory();
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
        var queue = localQueue("faulted-lane");
        faultTerminally(queue);
        queue.HasFaulted.ShouldBeTrue();

        var session = await _host.TrackActivity().Timeout(10.Seconds())
            .PublishMessageAndWaitAsync(new FaultedLaneMessage());

        session.Executed.SingleMessage<FaultedLaneMessage>().ShouldNotBeNull();
        queue.HasFaulted.ShouldBeFalse();
    }

    [Fact]
    public async Task the_agents_queue_recovers_from_a_fault()
    {
        var queue = localQueue(TransportConstants.Agents);
        faultTerminally(queue);

        Should.NotThrow(() => queue.Enqueue(new Envelope(new FaultedLaneMessage())));
        queue.HasFaulted.ShouldBeFalse();
    }

    private BufferedLocalQueue localQueue(string name)
    {
        return theRuntime.Endpoints.AgentForLocalQueue(name).ShouldBeOfType<BufferedLocalQueue>();
    }

    // Fault the real block the way its consumer loop does when an exception escapes it, e.g. an
    // OutOfMemoryException thrown from WaitToReadAsync
    private static void faultTerminally(BufferedReceiver receiver)
    {
        var block = (Block<Envelope>)typeof(BufferedReceiver)
            .GetField("_receivingBlock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(receiver)!;

        typeof(Block<Envelope>)
            .GetMethod("fault", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(block, [new OutOfMemoryException()]);

        block.Failure.ShouldNotBeNull();
    }
}

public record FaultedLaneMessage;

public static class FaultedLaneHandler
{
    public static void Handle(FaultedLaneMessage message)
    {
    }
}
