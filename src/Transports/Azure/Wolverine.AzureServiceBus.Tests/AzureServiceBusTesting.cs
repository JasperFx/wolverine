using Azure.Messaging.ServiceBus.Administration;

namespace Wolverine.AzureServiceBus.Tests;

public static class AzureServiceBusTesting
{
    private static bool _cleaned;

    public static AzureServiceBusConfiguration UseAzureServiceBusTesting(this WolverineOptions options)
    {
        if (!_cleaned)
        {
            _cleaned = true;
            DeleteAllEmulatorObjectsAsync().GetAwaiter().GetResult();
        }

        var config = options.UseAzureServiceBus(ServiceBusContainerFixture.ConnectionString);

        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();

        return config.AutoProvision();
    }

    public static async Task DeleteAllEmulatorObjectsAsync()
    {
        var client = new ServiceBusAdministrationClient(ServiceBusContainerFixture.ConnectionString);

        await foreach (var topic in client.GetTopicsAsync())
        {
            await client.DeleteTopicAsync(topic.Name);
        }

        await foreach (var queue in client.GetQueuesAsync())
        {
            await client.DeleteQueueAsync(queue.Name);
        }
    }
}
