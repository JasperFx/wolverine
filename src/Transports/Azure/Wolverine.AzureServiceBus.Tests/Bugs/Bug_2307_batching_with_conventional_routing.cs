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

[Trait("Category", "Flaky")]
public class Bug_2307_batching_with_conventional_routing : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
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

    public async Task DisposeAsync()
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
