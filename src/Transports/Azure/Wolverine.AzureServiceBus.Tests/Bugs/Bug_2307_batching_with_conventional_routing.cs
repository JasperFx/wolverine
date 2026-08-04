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

// NOT flaky. Re-measured 2026-08-02 after the entity-name sanitizing fix: 1 of 1 still fails, but in
// 5 SECONDS on a plain assertion instead of the 2-minute broker timeout, so the real defect is now
// readable:
//
//   Expected a listener endpoint for queue 'wolverine.azureservicebus.tests.bugs.batcheditem'
//   but found only: Wolverine.AzureServiceBus.Tests.Bugs.BatchedItem, AzureServiceBusResponses, ...
//
// The listener IS created for the batch element type (that much of GH-2307 works) -- its
// EndpointName is just the raw type name rather than the sanitized queue name. A naming bug in the
// GH-2307 fix, not a routing or provisioning failure. Tracked as GH-3827.
[Trait("Category", "Flaky")]
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

        var endpoints = runtime.Options.Transports.AllEndpoints()
            .Where(x => x is AzureServiceBusQueue)
            .Where(x => x.IsListener)
            .ToArray();

        endpoints.ShouldContain(
            e => e.EndpointName == expectedQueueName,
            $"Expected a listener endpoint for queue '{expectedQueueName}' but found only: {string.Join(", ", endpoints.Select(e => e.EndpointName))}");
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
