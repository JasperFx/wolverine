using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace Wolverine.AzureServiceBus;

/// <summary>
/// Helpers for connecting Wolverine to a locally running
/// <a href="https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator">Azure Service Bus emulator</a>.
/// The emulator exposes messaging (AMQP) and management (HTTP) on two different ports, so it requires
/// both a messaging connection string and a separate management connection string.
/// </summary>
public static class AzureServiceBusEmulatorExtensions
{
    /// <summary>
    /// The default messaging (AMQP) connection string for a locally hosted Azure Service Bus emulator
    /// listening on the standard port 5672
    /// </summary>
    public const string DefaultEmulatorConnectionString =
        "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    /// <summary>
    /// The default management (HTTP) connection string for a locally hosted Azure Service Bus emulator
    /// listening on the standard port 5300
    /// </summary>
    public const string DefaultEmulatorManagementConnectionString =
        "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    /// <summary>
    /// Connect to a locally running Azure Service Bus emulator using the standard emulator connection
    /// strings (AMQP on localhost:5672, management on localhost:5300). Use the overload with explicit
    /// connection strings if you have mapped the emulator to different ports.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="configure">Optionally configure the underlying <see cref="ServiceBusClientOptions"/></param>
    /// <returns></returns>
    public static AzureServiceBusConfiguration UseAzureServiceBusEmulator(this WolverineOptions endpoints,
        Action<ServiceBusClientOptions>? configure = null)
    {
        return endpoints.UseAzureServiceBusEmulator(DefaultEmulatorConnectionString,
            DefaultEmulatorManagementConnectionString, configure);
    }

    /// <summary>
    /// Connect to a locally running Azure Service Bus emulator with explicit messaging (AMQP) and
    /// management (HTTP) connection strings
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="connectionString">The messaging (AMQP) connection string</param>
    /// <param name="managementConnectionString">The management (HTTP) connection string used for administration</param>
    /// <param name="configure">Optionally configure the underlying <see cref="ServiceBusClientOptions"/></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static AzureServiceBusConfiguration UseAzureServiceBusEmulator(this WolverineOptions endpoints,
        string connectionString, string managementConnectionString, Action<ServiceBusClientOptions>? configure = null)
    {
        if (managementConnectionString == null)
        {
            throw new ArgumentNullException(nameof(managementConnectionString));
        }

        var configuration = endpoints.UseAzureServiceBus(connectionString, configure);

        endpoints.AzureServiceBusTransport().ManagementConnectionString = managementConnectionString;

        return configuration;
    }

    /// <summary>
    /// CAUTION!!! This deletes every queue and topic in the targeted Azure Service Bus namespace. This is
    /// meant strictly for local development and testing against the Azure Service Bus emulator.
    /// </summary>
    /// <param name="managementConnectionString">The management (HTTP) connection string of the emulator</param>
    /// <param name="token"></param>
    public static async Task DeleteAllAzureServiceBusObjectsAsync(
        string managementConnectionString = DefaultEmulatorManagementConnectionString,
        CancellationToken token = default)
    {
        var client = new ServiceBusAdministrationClient(managementConnectionString);

        await foreach (var topic in client.GetTopicsAsync(token).WithCancellation(token))
        {
            await client.DeleteTopicAsync(topic.Name, token);
        }

        await foreach (var queue in client.GetQueuesAsync(token).WithCancellation(token))
        {
            await client.DeleteQueueAsync(queue.Name, token);
        }
    }
}
