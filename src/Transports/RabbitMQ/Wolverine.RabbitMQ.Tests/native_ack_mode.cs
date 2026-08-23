using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

#region messages and handler

public record NativeAckWork(int Number);

public static class NativeAckWorkTracking
{
    public static readonly ConcurrentBag<int> Handled = new();

    /// <summary>When set, handlers park here forever -- standing in for a node that dies mid-flight.</summary>
    public static TaskCompletionSource? Block;

    public static void Reset()
    {
        Handled.Clear();
        Block = null;
    }
}

public class NativeAckWorkHandler
{
    public async Task Handle(NativeAckWork message)
    {
        if (NativeAckWorkTracking.Block != null)
        {
            await NativeAckWorkTracking.Block.Task;
        }

        NativeAckWorkTracking.Handled.Add(message.Number);
    }
}

#endregion

/// <summary>
/// GH-3708. RabbitMQ is the first transport to opt into EndpointMode.NativeAck. The guarantee under test is the
/// one the mode exists for: Buffered's throughput with Inline's no-loss behaviour.
/// </summary>
public class native_ack_mode : IAsyncLifetime
{
    // A queue per test. These share one static tracking bag and one broker, so a single queue let the
    // redelivery test's messages land in another test's assertion.
    private const string ModeQueue = "native-ack-3708-mode";
    private const string EndToEndQueue = "native-ack-3708-e2e";
    private const string RedeliveryQueue = "native-ack-3708-redelivery";

    public ValueTask InitializeAsync()
    {
        NativeAckWorkTracking.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        NativeAckWorkTracking.Block?.TrySetResult();
        NativeAckWorkTracking.Reset();
        return ValueTask.CompletedTask;
    }

    private static Task<IHost> startHostAsync(string queueName)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq("host=localhost;port=5672").AutoProvision();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<NativeAckWorkHandler>();

                opts.ListenToRabbitQueue(queueName)
                    .Named(queueName)
                    .ProcessInParallelWithNativeAcks();

                opts.PublishMessage<NativeAckWork>().ToRabbitQueue(queueName);
            }).StartAsync();
    }

    [Fact]
    public async Task the_endpoint_really_is_in_native_ack_mode_with_a_native_ack_receiver()
    {
        using var host = await startHostAsync(ModeQueue);
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        var endpoint = runtime.Endpoints.EndpointByName(ModeQueue).ShouldNotBeNull();
        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);

        // Prefetch IS the back pressure for this mode, so it must cover every lane that can be busy
        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();
    }

    [Fact]
    public async Task messages_are_processed_end_to_end()
    {
        using var host = await startHostAsync(EndToEndQueue);
        var bus = host.MessageBus();

        for (var i = 0; i < 10; i++)
        {
            await bus.SendAsync(new NativeAckWork(i));
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (handledInRange(0, 10).Count() < 10 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        handledInRange(0, 10).ShouldBe(Enumerable.Range(0, 10));
    }

    /// <summary>
    /// The whole point of the mode. Under BufferedInMemory these messages are acked the instant they arrive, so
    /// a node that dies before the handler finishes loses every one of them. Under NativeAck nothing is acked
    /// until the handler succeeds, so closing the channel hands them all back to the broker.
    /// </summary>
    [Fact]
    public async Task nothing_is_acked_until_the_handler_succeeds_so_a_dead_node_loses_nothing()
    {
        NativeAckWorkTracking.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstHost = await startHostAsync(RedeliveryQueue);
        var bus = firstHost.MessageBus();

        for (var i = 0; i < 5; i++)
        {
            await bus.SendAsync(new NativeAckWork(100 + i));
        }

        // Let the deliveries actually reach the parked handlers before killing the node
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (queueCount(firstHost, RedeliveryQueue) == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // The node dies mid-flight, having acked nothing
        firstHost.Dispose();

        handledInRange(100, 5).ShouldBeEmpty();

        // A fresh node picks up every redelivered message
        NativeAckWorkTracking.Block!.TrySetResult();
        NativeAckWorkTracking.Block = null;

        using var secondHost = await startHostAsync(RedeliveryQueue);

        deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (handledInRange(100, 5).Count() < 5 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        handledInRange(100, 5).ShouldBe(Enumerable.Range(100, 5));
    }

    private static IEnumerable<int> handledInRange(int start, int count)
    {
        return NativeAckWorkTracking.Handled
            .Where(x => x >= start && x < start + count)
            .Distinct()
            .OrderBy(x => x);
    }

    private static int queueCount(IHost host, string queueName)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var circuit = runtime.Endpoints.FindListenerCircuit(new Uri($"rabbitmq://queue/{queueName}"));
        return circuit?.QueueCount ?? 0;
    }
}
