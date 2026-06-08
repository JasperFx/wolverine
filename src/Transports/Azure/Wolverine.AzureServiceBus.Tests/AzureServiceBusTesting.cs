using Azure.Messaging.ServiceBus.Administration;
using IntegrationTests;

namespace Wolverine.AzureServiceBus.Tests;

public static class AzureServiceBusTesting
{
    private static bool _cleaned;

    public static AzureServiceBusConfiguration UseAzureServiceBusTesting(this WolverineOptions options)
    {
        if (!_cleaned)
        {
            _cleaned = true;
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            DeleteAllEmulatorObjectsAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
        }

        var config = options.UseAzureServiceBus(Servers.AzureServiceBusConnectionString);

        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();
        transport.ManagementConnectionString = Servers.AzureServiceBusManagementConnectionString;

        return config.AutoProvision();
    }

    public static async Task DeleteAllEmulatorObjectsAsync()
        => await DeleteAllEmulatorObjectsAsync(Servers.AzureServiceBusManagementConnectionString);

    public static async Task DeleteAllEmulatorObjectsAsync(string connectionString)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var client = new ServiceBusAdministrationClient(connectionString);

        await foreach (var topic in client.GetTopicsAsync().WithCancellation(ct))
        {
            await client.DeleteTopicAsync(topic.Name, ct);
        }

        await foreach (var queue in client.GetQueuesAsync().WithCancellation(ct))
        {
            await client.DeleteQueueAsync(queue.Name, ct);
        }
    }
}
