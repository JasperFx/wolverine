using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Bugs;

// Regression test for GH-4868: the idle sending agent reaper stranding request/reply to an endpoint that is
// only ever addressed through IMessageBus.EndpointFor(uri) -- which is exactly how every node agent
// command (StartAgents, StopAgents, QueryAgentPresence, ...) travels to a peer's control queue.
//
// executeIdleSendingAgentCleanup disposes any non-durable sending agent that has sent nothing for
// SendingAgentIdleTimeout (5 minutes by default). GH-1908 wrote it for precisely these control and
// reply queues, so that a DEPARTED node's sender does not leak forever, and it must keep doing that.
// A settled cluster's leader sends nothing to a peer's control queue for hours, so the sender is
// reaped long before the next scale event needs it.
//
// GH-3955 made the endpoint itself recover: RemoveSendingAgentAsync nulls Endpoint.Agent, and the
// next EndpointFor(uri) builds a fresh agent. But Endpoint.Routes caches one MessageRoute per message
// type, and each MessageRoute captured the ISendingAgent at construction. DestinationEndpoint.InvokeAsync
// goes through that cache, so the request was handed to the DISPOSED agent: "Enqueued for sending" is
// logged, the RetryBlock runs the send inline, BatchedSender posts onto a completed Block, and
// Block.PostAsync returns silently because Complete() latched it. No exception, no "Successfully sent",
// nothing on the broker -- just a reply timeout, for every request, until the process restarts.
public class Bug_4868_idle_reaper_strands_cached_route_sender : IAsyncLifetime
{
    private IHost _receiver = null!;
    private IHost? _sender;
    private Uri _receiverUri = null!;

    public async ValueTask InitializeAsync()
    {
        var receiverPort = PortFinder.GetAvailablePort();
        _receiverUri = $"tcp://localhost:{receiverPort}".ToUri();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Receiver";
                opts.DisableConventionalDiscovery();
                opts.IncludeType<ReapedRoutePingHandler>();
                opts.ListenAtPort(receiverPort);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender != null)
        {
            await _sender.StopAsync();
            _sender.Dispose();
        }

        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    private async Task<IHost> startSenderAsync(TimeSpan idleTimeout)
    {
        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Sender";
                opts.DisableConventionalDiscovery();

                // Replies come back to this listener
                opts.ListenAtPort(PortFinder.GetAvailablePort());

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.SendingAgentIdleTimeout = idleTimeout;
            }).StartAsync();

        return _sender;
    }

    private Task<ReapedRoutePong> invokeAsync(string name)
    {
        // Short enough to fail the test promptly before the fix, long enough for an honest TCP round trip
        return _sender!.MessageBus().EndpointFor(_receiverUri)
            .InvokeAsync<ReapedRoutePong>(new ReapedRoutePing(name), TestContext.Current.CancellationToken,
                5.Seconds());
    }

    [Fact]
    public async Task remote_invocation_still_works_after_the_sending_agent_is_removed_by_hand()
    {
        // Idle timeout long enough that the reaper plays no part here; the removal is done by hand,
        // which is both deterministic and the exact code path the reaper takes.
        var sender = await startSenderAsync(5.Minutes());
        var runtime = sender.GetRuntime();

        (await invokeAsync("before")).Name.ShouldBe("before");

        var endpoint = runtime.Endpoints.EndpointFor(_receiverUri)!;
        var original = endpoint.RouteFor(typeof(ReapedRoutePing), runtime).Sender;
        original.ShouldNotBeNull();

        await ((EndpointCollection)runtime.Endpoints).RemoveSendingAgentAsync(_receiverUri);
        endpoint.Agent.ShouldBeNull();

        // The next use rebuilds the agent...
        var rebuilt = runtime.Endpoints.GetOrBuildSendingAgent(_receiverUri);
        rebuilt.ShouldNotBeSameAs(original);

        // ...and the cached route has to follow it rather than keep handing out the disposed one
        endpoint.RouteFor(typeof(ReapedRoutePing), runtime).Sender.ShouldBeSameAs(rebuilt);

        (await invokeAsync("after")).Name.ShouldBe("after");
    }

    [Fact]
    public async Task remote_invocation_still_works_after_the_idle_reaper_runs()
    {
        var sender = await startSenderAsync(1.Seconds());
        var runtime = sender.GetRuntime();

        (await invokeAsync("first")).Name.ShouldBe("first");

        // The reaper SHOULD remove this agent. An endpoint reached only through EndpointFor(uri) has no
        // subscriptions, and GH-1908 exists so that a departed node's control queue sender does not leak.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (runtime.Endpoints.ActiveSendingAgents().Any(x => x.Destination == _receiverUri))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    "The idle sending agent reaper never removed the sending agent for " + _receiverUri);
            }

            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        // Before the fix this timed out: the request went to the disposed agent and never left the process
        (await invokeAsync("second")).Name.ShouldBe("second");
    }
}

public record ReapedRoutePing(string Name);

public record ReapedRoutePong(string Name);

public class ReapedRoutePingHandler
{
    public static ReapedRoutePong Handle(ReapedRoutePing ping)
    {
        return new ReapedRoutePong(ping.Name);
    }
}
