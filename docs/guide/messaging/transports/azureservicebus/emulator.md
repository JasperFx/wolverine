# Using the Azure Service Bus Emulator

The [Azure Service Bus Emulator](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator) allows you to run integration tests against a local emulator instance instead of a real Azure Service Bus namespace. This is exactly what Wolverine uses internally for its own test suite.

## Docker Compose Setup

The Azure Service Bus Emulator requires a SQL Server backend. Here is a minimal Docker Compose setup:

```yaml
networks:
  sb-emulator:

services:
  asb-sql:
    image: "mcr.microsoft.com/azure-sql-edge"
    environment:
      - "ACCEPT_EULA=Y"
      - "MSSQL_SA_PASSWORD=Strong_Passw0rd#2025"
    networks:
      sb-emulator:

  asb-emulator:
    image: "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest"
    volumes:
      - ./docker/asb/Config.json:/ServiceBus_Emulator/ConfigFiles/Config.json
    ports:
      - "5673:5672"   # AMQP messaging
      - "5300:5300"   # HTTP management
    environment:
      SQL_SERVER: asb-sql
      MSSQL_SA_PASSWORD: "Strong_Passw0rd#2025"
      ACCEPT_EULA: "Y"
      EMULATOR_HTTP_PORT: 5300
    depends_on:
      - asb-sql
    networks:
      sb-emulator:
```

::: tip
The emulator exposes two ports: the AMQP port (5672) for sending and receiving messages, and an HTTP management port (5300) for queue/topic administration. These must be mapped to different host ports.
:::

## Emulator Configuration File

The emulator reads a `Config.json` file on startup. A minimal configuration that lets Wolverine auto-provision everything it needs:

```json
{
  "UserConfig": {
    "Namespaces": [
      {
        "Name": "sbemulatorns"
      }
    ],
    "Logging": {
      "Type": "File"
    }
  }
}
```

You can also pre-configure queues and topics in this file if needed:

```json
{
  "UserConfig": {
    "Namespaces": [
      {
        "Name": "sbemulatorns",
        "Queues": [
          {
            "Name": "my-queue",
            "Properties": {
              "MaxDeliveryCount": 3,
              "LockDuration": "PT1M",
              "RequiresSession": false
            }
          }
        ],
        "Topics": [
          {
            "Name": "my-topic",
            "Subscriptions": [
              {
                "Name": "my-subscription",
                "Properties": {
                  "MaxDeliveryCount": 3,
                  "LockDuration": "PT1M"
                }
              }
            ]
          }
        ]
      }
    ],
    "Logging": {
      "Type": "File"
    }
  }
}
```

## Connection Strings

The emulator uses standard Azure Service Bus connection strings with `UseDevelopmentEmulator=true`:

```cs
// AMQP connection for sending/receiving messages
var messagingConnectionString =
    "Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

// HTTP connection for management operations (creating queues, topics, etc.)
var managementConnectionString =
    "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
```

::: warning
The emulator uses separate ports for messaging (AMQP) and management (HTTP) operations. In production Azure Service Bus, a single connection string handles both, but the emulator requires you to configure these separately.
:::

## Configuring Wolverine with the Emulator

The key to using the emulator with Wolverine is setting both the primary connection string (for AMQP messaging) and the `ManagementConnectionString` (for HTTP administration) on the transport:

```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UseAzureServiceBus(messagingConnectionString)
        .AutoProvision()
        .AutoPurgeOnStartup();

    // Required for the emulator: set the management connection string
    // to the HTTP port since it differs from the AMQP port
    var transport = opts.Transports.GetOrCreate<AzureServiceBusTransport>();
    transport.ManagementConnectionString = managementConnectionString;

    // Configure your queues, topics, etc. as normal
    opts.ListenToAzureServiceBusQueue("my-queue");
    opts.PublishAllMessages().ToAzureServiceBusQueue("my-queue");
});
```

## Creating a Test Helper

Wolverine's own test suite uses a static helper extension method to standardize emulator configuration across all tests. Here's the pattern:

```cs
public static class AzureServiceBusTesting
{
    // Connection strings pointing at the emulator
    public static readonly string MessagingConnectionString =
        "Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    public static readonly string ManagementConnectionString =
        "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private static bool _cleaned;

    public static AzureServiceBusConfiguration UseAzureServiceBusTesting(
        this WolverineOptions options)
    {
        // Delete all queues and topics on first usage to start clean
        if (!_cleaned)
        {
            _cleaned = true;
            DeleteAllEmulatorObjectsAsync().GetAwaiter().GetResult();
        }

        var config = options.UseAzureServiceBus(MessagingConnectionString);

        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();
        transport.ManagementConnectionString = ManagementConnectionString;

        return config.AutoProvision();
    }

    public static async Task DeleteAllEmulatorObjectsAsync()
    {
        var client = new ServiceBusAdministrationClient(ManagementConnectionString);

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
```

## Writing Integration Tests

With the helper in place, integration tests become straightforward:

```cs
public class when_sending_messages : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("send_and_receive");
                opts.PublishMessage<MyMessage>()
                    .ToAzureServiceBusQueue("send_and_receive");
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task send_and_receive_a_single_message()
    {
        var message = new MyMessage("Hello");

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<MyMessage>()
            .Name.ShouldBe("Hello");
    }
}
```

::: tip
Use `.IncludeExternalTransports()` on the tracked session so Wolverine waits for messages that travel through Azure Service Bus rather than only tracking in-memory activity.
:::

## Disabling Parallel Test Execution

Because the emulator is a shared resource, tests that create and tear down queues or topics can interfere with each other when run in parallel. Wolverine's own test suite disables parallel execution for its Azure Service Bus tests:

```cs
// Add to a file like NoParallelization.cs in your test project
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
```
