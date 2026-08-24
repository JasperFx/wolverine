using System.Collections.Concurrent;
using Amazon.SQS;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Tests;

#region messages, handler and per-node tracking

public record NativeAckSqsWork(int Number);

/// <summary>
///     One marker per host, so every observation records WHICH node made it. That is what keeps the redelivery
///     test honest: the first node's parked handlers eventually unwind and record too, and without a node name
///     those late recordings would satisfy an assertion that is supposed to be about the second node.
/// </summary>
public class NativeAckSqsNode(string name, bool parks)
{
    public string Name { get; } = name;
    public bool Parks { get; } = parks;
}

public static class NativeAckSqsTracking
{
    public static readonly ConcurrentBag<(string Node, int Number)> Started = new();
    public static readonly ConcurrentBag<(string Node, int Number)> Handled = new();

    /// <summary>When set, handlers on a parking node wait here forever -- standing in for a node that dies mid-flight.</summary>
    public static TaskCompletionSource? Block;

    public static void Reset()
    {
        Started.Clear();
        Handled.Clear();
        Block = null;
    }

    public static IEnumerable<int> HandledBy(string node, int start, int count)
    {
        return Handled.Where(x => x.Node == node && x.Number >= start && x.Number < start + count)
            .Select(x => x.Number).Distinct().OrderBy(x => x);
    }

    public static int StartedOn(string node)
    {
        return Started.Where(x => x.Node == node).Select(x => x.Number).Distinct().Count();
    }
}

public class NativeAckSqsWorkHandler
{
    public async Task Handle(NativeAckSqsWork message, NativeAckSqsNode node)
    {
        NativeAckSqsTracking.Started.Add((node.Name, message.Number));

        if (node.Parks && NativeAckSqsTracking.Block is { } block)
        {
            await block.Task;
        }

        NativeAckSqsTracking.Handled.Add((node.Name, message.Number));
    }
}

#endregion

/// <summary>
///     GH-4050. Amazon SQS opts into <see cref="EndpointMode.NativeAck" />. These are the #3708 expectations
///     against LocalStack -- the mode is really in force, messages flow end to end, and above all a node that dies
///     with work in its lanes hands every one of those deliveries back to SQS.
/// </summary>
/// <remarks>
///     Every test gets its own queue name. These share one static tracking bag and one broker, and on the RabbitMQ
///     version of this suite a single shared queue let the redelivery test's messages land in another test's
///     assertion.
/// </remarks>
public class native_ack_mode : IAsyncLifetime
{
    // Short enough to keep the redelivery test quick, long enough that LocalStack's second-granularity
    // visibility clock is not the thing being measured
    private const int VisibilityTimeoutSeconds = 5;

    private readonly string _modeQueue = uniqueQueue("native-ack-4050-mode");
    private readonly string _endToEndQueue = uniqueQueue("native-ack-4050-e2e");
    private readonly string _redeliveryQueue = uniqueQueue("native-ack-4050-redelivery");
    private readonly string _fifoQueue = uniqueQueue("native-ack-4050-fifo") + ".fifo";

    private static string uniqueQueue(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    public ValueTask InitializeAsync()
    {
        NativeAckSqsTracking.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // Let any handler still parked from a dead node unwind, so the test run does not leak them
        NativeAckSqsTracking.Block?.TrySetResult();
        NativeAckSqsTracking.Reset();
        return ValueTask.CompletedTask;
    }

    private static Task<IHost> startHostAsync(string queueName, string nodeName, bool parks = false,
        int? maxParallelism = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoProvision();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<NativeAckSqsWorkHandler>();
                opts.Services.AddSingleton(new NativeAckSqsNode(nodeName, parks));

                // Bound how long a stop can sit on a lane full of parked handlers
                opts.Durability.DrainTimeout = 2.Seconds();

                opts.ListenToSqsQueue(queueName, q =>
                    {
                        q.VisibilityTimeout = VisibilityTimeoutSeconds;
                        q.WaitTimeSeconds = 1;
                    })
                    .Named(queueName)
                    .ProcessInParallelWithNativeAcks()
                    .MaximumParallelMessages(maxParallelism ?? Math.Max(Environment.ProcessorCount, 5));

                // Deliberately NOT SendInline(): the listening and sending sides are the same AmazonSqsQueue
                // object here, and SendInline() would assign EndpointMode.Inline right back over the mode under
                // test.
                opts.PublishMessage<NativeAckSqsWork>().ToSqsQueue(queueName);
            }).StartAsync();
    }

    [Fact]
    public async Task the_endpoint_really_is_in_native_ack_mode()
    {
        // Three lanes rather than the default: the derived receive batch size is then 6, which is distinct from
        // BOTH the SQS maximum of 10 and the default this property has in every other mode. On a machine with five
        // or more cores the default parallelism would clamp to 10 and this assertion would be true either way.
        using var host = await startHostAsync(_modeQueue, "only", maxParallelism: 3);
        try
        {
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var endpoint = runtime.Endpoints.EndpointByName(_modeQueue).ShouldNotBeNull();

            endpoint.Mode.ShouldBe(EndpointMode.NativeAck);

            // Back pressure is the broker's delivery window, so no BackPressureAgent is created
            endpoint.ShouldEnforceBackPressure().ShouldBeFalse();

            // ... which makes the receive batch size the whole of the back pressure story on SQS
            var queue = endpoint.ShouldBeOfType<AmazonSqsQueue>();
            queue.MaxDegreeOfParallelism.ShouldBe(3);
            queue.MaxNumberOfMessages.ShouldBe(6);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task messages_are_processed_end_to_end()
    {
        using var host = await startHostAsync(_endToEndQueue, "only");
        try
        {
            var bus = host.MessageBus();
            for (var i = 0; i < 10; i++)
            {
                await bus.SendAsync(new NativeAckSqsWork(i));
            }

            await waitFor(() => NativeAckSqsTracking.HandledBy("only", 0, 10).Count() >= 10, 60.Seconds());

            NativeAckSqsTracking.HandledBy("only", 0, 10).ShouldBe(Enumerable.Range(0, 10));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     The whole point of the mode, and the one SQS-specific thing worth proving: under BufferedInMemory these
    ///     messages are deleted the instant they arrive, so a node that dies before its handlers finish loses every
    ///     one of them. Under NativeAck nothing is deleted until the handler succeeds, so the deliveries are still
    ///     unsettled when the node goes away and SQS makes them visible again at their visibility timeout.
    /// </summary>
    /// <remarks>
    ///     Deliberately asserted per node. When the parked handlers on the dead node eventually unwind they record
    ///     too, so an assertion that only counted message numbers would pass whether or not SQS redelivered
    ///     anything. Only the SECOND node's recordings can come from a redelivery.
    /// </remarks>
    [Fact]
    public async Task nothing_is_settled_until_the_handler_succeeds_so_a_draining_node_loses_nothing()
    {
        NativeAckSqsTracking.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstHost = await startHostAsync(_redeliveryQueue, "first", parks: true);

        var bus = firstHost.MessageBus();
        for (var i = 0; i < 5; i++)
        {
            await bus.SendAsync(new NativeAckSqsWork(100 + i));
        }

        // Let every delivery actually reach a parked handler before killing the node
        await waitFor(() => NativeAckSqsTracking.StartedOn("first") >= 5, 60.Seconds());
        NativeAckSqsTracking.StartedOn("first").ShouldBe(5);

        // The node dies mid-flight, having settled nothing
        await firstHost.StopAsync(TestContext.Current.CancellationToken);
        firstHost.Dispose();

        NativeAckSqsTracking.HandledBy("first", 100, 5).ShouldBeEmpty();

        // A fresh node picks up every message SQS made visible again. Its handlers do not park -- and the first
        // node's still do, so anything it records later is still attributed to "first".
        using var secondHost = await startHostAsync(_redeliveryQueue, "second");
        try
        {
            await waitFor(() => NativeAckSqsTracking.HandledBy("second", 100, 5).Count() >= 5, 90.Seconds());

            NativeAckSqsTracking.HandledBy("second", 100, 5).ShouldBe(Enumerable.Range(100, 5));
        }
        finally
        {
            await secondHost.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     GH-4050. The FIFO verdict, proved through a real bootstrap rather than only against the validator, so
    ///     that a rule which stopped being wired in would be caught. See
    ///     <c>AmazonSqsQueue.FifoQueuesAreIncompatibleWithNativeAcks</c> for the reasoning.
    /// </summary>
    [Fact]
    public async Task a_fifo_queue_refuses_the_mode_at_bootstrap()
    {
        var ex = await Should.ThrowAsync<InvalidListenerConfigurationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseAmazonSqsTransportLocally().AutoProvision();
                    opts.Discovery.DisableConventionalDiscovery().IncludeType<NativeAckSqsWorkHandler>();
                    opts.Services.AddSingleton(new NativeAckSqsNode("fifo", false));

                    opts.ListenToSqsQueue(_fifoQueue, q =>
                        {
                            q.Configuration.Attributes ??= new Dictionary<string, string>();
                            q.Configuration.Attributes[QueueAttributeName.FifoQueue] = "true";
                            q.Configuration.Attributes[QueueAttributeName.ContentBasedDeduplication] = "true";
                        })
                        .ProcessInParallelWithNativeAcks();
                }).StartAsync();
        });

        ex.Message.ShouldContain(_fifoQueue);
        ex.Message.ShouldContain("ProcessInline()");
    }

    private static async Task waitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
