using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Runtime;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Bugs;

// GH-3827: UNTAGGED. This was never a product bug -- the assertion was reading the wrong property.
//
// The 2026-08-02 triage was right that the listener IS created for the batch element type (so GH-2307
// works) and right that the failure was readable rather than a broker timeout, but it concluded the
// endpoint had been given "the raw type name rather than the sanitized queue name". Dumping the
// endpoints shows both names are exactly what they should be:
//
//   EndpointName='Wolverine.AzureServiceBus.Tests.Bugs.BatchedItem'
//   Uri='asb://queue/wolverine.azureservicebus.tests.bugs.batcheditem'   <- the real queue, sanitized
//
// EndpointName is a LOGICAL name and is deliberately not the entity name: MessageRoutingConvention
// has `endpoint.EndpointName = queueName` commented out on both listener paths, and the transport's
// own system endpoints do the same thing (AzureServiceBusResponses ->
// asb://queue/wolverine.response.*). AzureServiceBusQueue.QueueName is the physical entity name and
// is set once at construction; EndpointName is mutable and gets the friendly value afterwards.
//
// So the test asserts on QueueName now, which is the thing GH-2307 is actually about.
public class Bug_2307_batching_with_conventional_routing : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .UseConventionalRouting(x => x.IncludeTypes(t => t == typeof(BatchedItem)))
                    .AutoProvision();

                opts.BatchMessagesOf<BatchedItem>(batching =>
                {
                    batching.BatchSize = 5;
                    batching.TriggerTime = 3.Seconds();
                });

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public void conventional_routing_should_create_listener_for_batch_element_type()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        // The batch element type should have a listener endpoint created by conventional routing.
        // Without the fix, only the array type (BatchedItem[]) gets a listener, not the element type.
        var expectedQueueName = typeof(BatchedItem).ToMessageTypeName().ToLowerInvariant();

        // QueueName, not EndpointName: the former is the Azure Service Bus entity this convention is
        // supposed to have created, the latter is a friendly label that deliberately keeps the
        // unsanitized type name. See the note on this class.
        var queues = runtime.Options.Transports.AllEndpoints()
            .OfType<AzureServiceBusQueue>()
            .Where(x => x.IsListener)
            .ToArray();

        queues.ShouldContain(
            q => q.QueueName == expectedQueueName,
            $"Expected a listener for Azure Service Bus queue '{expectedQueueName}' but found only: {string.Join(", ", queues.Select(q => q.QueueName))}");
    }
}

public record BatchedItem(string Name);

public static class BatchedItemHandler
{
    public static void Handle(BatchedItem[] items)
    {
        // batch handler
    }
}
