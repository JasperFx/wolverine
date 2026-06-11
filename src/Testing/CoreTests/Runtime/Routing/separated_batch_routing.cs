using CoreTests.Acceptance;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Runtime.Routing;

// Routing and executor-resolution mechanics for the Separated-mode batching scenarios whose
// end-to-end behavior is covered in CoreTests.Acceptance.batching_with_separated_handlers. These
// assert how a conflicting element type is routed (fan-out to the dedicated -batch queue) and how
// the dedicated batch queue / external listener / produced-batch queue resolve to the right handler.
//
// Shares the handler + message types (LoadEvent/InvoiceEvent ...) with the batching suite.
public class separated_batch_routing
{
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
        batchHandler.ShouldBeOfType<BatchingProcessor<LoadEvent>>();
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

    [Fact]
    public async Task produced_array_fans_out_to_each_sticky_handler_queue()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(InvoicePublisher))
                    .IncludeType(typeof(InvoiceArchiver));

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.BatchMessagesOf<InvoiceEvent>();
            })
            .StartAsync();

        var runtime = host.GetRuntime();

        // The batch's execution queue receives the produced InvoiceEvent[]...
        var batch = runtime.Options.BatchDefinitions.Single();
        var batchUri = new Uri("local://" + batch.LocalExecutionQueueName);
        var batchQueue = runtime.Endpoints.EndpointFor(batchUri)!;

        // ...but the two Handle(InvoiceEvent[]) handlers were separated onto their own sticky queues,
        // so the array must fan out from the batch queue to each of them.
        var handler = runtime.As<IExecutorFactory>().BuildFor(typeof(InvoiceEvent[]), batchQueue)
            .ShouldBeOfType<Executor>().Handler;
        handler.ShouldBeOfType<FanoutMessageHandler<InvoiceEvent[]>>();
    }
}
