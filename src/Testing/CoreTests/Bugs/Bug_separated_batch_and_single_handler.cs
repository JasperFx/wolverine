using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Bugs;

// Reproduction of the dom-order-api Funding scenario:
//   - MultipleHandlerBehavior.Separated
//   - BatchMessagesOf<LoadEvent>()
//   - LoadPublisher: batch handler  Handle(LoadEvent[] messages)
//   - LoadTelemetry: single handler Handle(LoadEvent e)
// Under Separated mode, a LoadEvent must invoke BOTH the direct handler AND the batch
// handler. Before the fix the direct handler silently shadowed the batch.
public class Bug_separated_batch_and_single_handler
{
    [Fact]
    public async Task separated_direct_and_batch_handler_both_run_on_local_publish()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(LoadPublisher))
                    .IncludeType(typeof(LoadTelemetry));

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.BatchMessagesOf<LoadEvent>(b => b.TriggerTime = 250.Milliseconds());
            })
            .StartAsync();

        LoadPublisher.BatchCalls.Clear();
        LoadTelemetry.SingleCalls.Clear();

        await host.TrackActivity()
            .Timeout(10.Seconds())
            .WaitForMessageToBeReceivedAt<LoadEvent[]>(host)
            .ExecuteAndWaitAsync(c => c.PublishAsync(new LoadEvent(1)));

        // The direct telemetry handler runs per-message...
        LoadTelemetry.SingleCalls.Count.ShouldBe(1);
        // ...and the batch publisher handler also runs on the batched array.
        LoadPublisher.BatchCalls.Count.ShouldBe(1);
        LoadPublisher.BatchCalls[0].Select(x => x.Id).ShouldBe([1]);
    }

    [Fact]
    public async Task batch_lives_on_its_own_queue_and_message_fans_out_to_both()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(LoadPublisher))
                    .IncludeType(typeof(LoadTelemetry));

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.BatchMessagesOf<LoadEvent>();
            })
            .StartAsync();

        var runtime = host.GetRuntime();

        // The batch was moved off the element type's default queue onto a dedicated one.
        var batch = runtime.Options.BatchDefinitions.Single();
        batch.LocalExecutionQueueName!.ShouldEndWith("-batch");
        var batchUri = new Uri("local://" + batch.LocalExecutionQueueName);

        // Routing a LoadEvent now fans out to BOTH the direct handler queue and the batch queue.
        var destinations = runtime.RoutingFor(typeof(LoadEvent))
            .ShouldBeOfType<MessageRouter<LoadEvent>>()
            .Routes.OfType<MessageRoute>().Select(x => x.Sender.Destination).ToArray();
        destinations.ShouldContain(batchUri);
        destinations.Length.ShouldBe(2);

        // The dedicated batch queue resolves to the batching processor (not the direct handler).
        var batchQueue = runtime.Endpoints.EndpointFor(batchUri)!;
        var batchHandler = runtime.As<IExecutorFactory>().BuildFor(typeof(LoadEvent), batchQueue)
            .ShouldBeOfType<Executor>().Handler;
        batchHandler.ShouldBeOfType<Wolverine.Runtime.Batching.BatchingProcessor<LoadEvent>>();
    }

    [Fact]
    public async Task separated_external_arrival_resolves_to_fanout_for_conflicting_element_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(LoadPublisher))
                    .IncludeType(typeof(LoadTelemetry));

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.BatchMessagesOf<LoadEvent>();

                // A non-local (external) listener — a LoadEvent arriving here must still
                // reach both the direct handler and the batch.
                opts.ListenForMessagesFrom("stub://external");
            })
            .StartAsync();

        var runtime = host.GetRuntime();
        var external = runtime.Options.Transports.AllEndpoints()
            .First(e => e is not LocalQueue && e.Uri.Scheme == "stub");

        // A LoadEvent arriving from an external listener relays to BOTH local queues
        // (direct + batch) via a fanout handler, so both run independently.
        var handler = runtime.As<IExecutorFactory>().BuildFor(typeof(LoadEvent), external)
            .ShouldBeOfType<Executor>().Handler;
        handler.ShouldBeOfType<FanoutMessageHandler<LoadEvent>>();
    }
}

public record LoadEvent(int Id);

public static class LoadPublisher
{
    public static readonly List<LoadEvent[]> BatchCalls = new();

    public static void Handle(LoadEvent[] messages)
    {
        BatchCalls.Add(messages);
    }
}

public static class LoadTelemetry
{
    public static readonly List<LoadEvent> SingleCalls = new();

    public static void Handle(LoadEvent e)
    {
        SingleCalls.Add(e);
    }
}
