using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4011. ListeningAgent.EnqueueDirectlyAsync is a type-switch over receiver implementations with a throwing
/// fallthrough. Every receiver that existed before GH-3708 had a branch, so the fallthrough was unreachable --
/// adding a fourth receiver type made it reachable. This is the durability agent's re-entry point (DLQ replay
/// per GH-1942, scheduled-message firing), so without a branch a NativeAck endpoint in an application that also
/// has persistence configured threw on any replay targeting it.
/// </summary>
public class native_ack_enqueue_directly_4011 : IAsyncLifetime
{
    private IHost _host = null!;
    private IWolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<NativeAckPingHandler>())
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = _host.Services.GetRequiredService<IWolverineRuntime>();
        NativeAckPingHandler.Handled.Clear();
        NativeAckPingHandler.Gate = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task enqueue_directly_reaches_a_native_ack_receiver_instead_of_throwing()
    {
        var endpoint = new NativeAckStubEndpoint("na-4011", new StubTransport())
        {
            IsListener = true
        };
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);

        await using var agent = new ListeningAgent(endpoint, (WolverineRuntime)theRuntime);
        await agent.StartAsync();

        // Guard against a vacuous pass: if this were a BufferedReceiver the existing first branch would have
        // handled it and the test would prove nothing about GH-4011.
        receiverOf(agent).ShouldBeOfType<NativeAckReceiver>();

        var envelope = new Envelope(new NativeAckPing("replayed"))
        {
            MessageType = typeof(NativeAckPing).ToMessageTypeName()
        };

        // Pre-GH-4011 this threw InvalidOperationException("There is no active, local queue for this listening
        // endpoint at ...") because NativeAckReceiver matched none of the branches.
        await agent.EnqueueDirectlyAsync([envelope]);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!NativeAckPingHandler.Handled.Contains("replayed") && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Yield();
        }

        NativeAckPingHandler.Handled.ShouldContain("replayed");
    }

    private static IReceiver receiverOf(ListeningAgent agent)
    {
        var field = typeof(ListeningAgent).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IReceiver)field.GetValue(agent)!;
    }
}
