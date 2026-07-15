# Using the Azure Service Bus Emulator

The [Azure Service Bus Emulator](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator) lets you run
Wolverine against a local emulator instance instead of a real Azure Service Bus namespace, either for local development or for
integration testing. This is exactly what Wolverine uses internally for its own test suite.

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
      - "5672:5672"   # AMQP messaging
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
The emulator exposes two ports: the AMQP port (5672) for sending and receiving messages, and an HTTP management port (5300) for
queue and topic administration. Wolverine's own `docker-compose.yml` maps the AMQP port to host port **5673** to avoid colliding
with RabbitMQ, which is why Wolverine's own tests pass explicit connection strings as shown below.
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

## Connecting Wolverine to the Emulator <Badge type="tip" text="6.18" />

The emulator uses one connection string for messaging (AMQP) and a *separate* one for management (HTTP), where real Azure Service
Bus uses a single connection string for both. `UseAzureServiceBusEmulator()` hides that split. With the standard emulator ports it
takes no arguments at all:

<!-- snippet: sample_using_azure_service_bus_emulator -->
<a id='snippet-sample_using_azure_service_bus_emulator'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // Connect to a locally running Azure Service Bus emulator using the
    // standard emulator ports (AMQP on 5672, management on 5300)
    opts.UseAzureServiceBusEmulator()

        // The emulator starts out empty, so let Wolverine build
        // any queues, topics, or subscriptions it needs
        .AutoProvision()
        .AutoPurgeOnStartup();

    opts.ListenToAzureServiceBusQueue("my-queue");
    opts.PublishAllMessages().ToAzureServiceBusQueue("my-queue");
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L48-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_azure_service_bus_emulator' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The parameterless version uses the defaults below, which are also exposed as the constants
`AzureServiceBusEmulatorExtensions.DefaultEmulatorConnectionString` and
`AzureServiceBusEmulatorExtensions.DefaultEmulatorManagementConnectionString`:

| Purpose | Default connection string |
| --- | --- |
| Messaging (AMQP) | `Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;` |
| Management (HTTP) | `Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;` |

If you have mapped the emulator to different host ports, pass both connection strings explicitly:

<!-- snippet: sample_using_azure_service_bus_emulator_with_connection_strings -->
<a id='snippet-sample_using_azure_service_bus_emulator_with_connection_strings'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // If you've mapped the emulator to non-standard ports, pass both the
    // messaging (AMQP) and management (HTTP) connection strings explicitly
    opts.UseAzureServiceBusEmulator(
            "Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
            "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;")
        .AutoProvision()
        .AutoPurgeOnStartup();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L74-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_azure_service_bus_emulator_with_connection_strings' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`UseAzureServiceBusEmulator()` returns the same `AzureServiceBusConfiguration` as `UseAzureServiceBus()`, so everything else --
`AutoProvision()`, `AutoPurgeOnStartup()`, conventional routing, queue and topic configuration -- chains off of it exactly as it
would against a real namespace.

## Deleting All Existing Objects at Startup

The emulator is a long lived, shared resource, and leftover queues or topics from earlier runs can leak into later ones. Wolverine
can wipe the namespace clean at application startup, but this behavior is strictly **opt in**:

<!-- snippet: sample_using_azure_service_bus_emulator_with_cleanup -->
<a id='snippet-sample_using_azure_service_bus_emulator_with_cleanup'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UseAzureServiceBusEmulator()

        // CAUTION! This deletes *every* queue and topic in the connected
        // namespace at startup. It is opt in, and is only meant for the
        // emulator or a throwaway namespace. Never turn this on against
        // a real Azure Service Bus namespace you care about
        .DeleteAllExistingObjectsOnStartup()

        .AutoProvision();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L96-L115' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_azure_service_bus_emulator_with_cleanup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: danger
`DeleteAllExistingObjectsOnStartup()` deletes **every** queue and topic -- and therefore every message and subscription -- in the
connected namespace each time the application starts. It is irreversible, and it is never turned on for you. It exists for the
emulator and for throwaway namespaces. Never enable it against a real Azure Service Bus namespace that holds anything you care
about. If you only want to drain messages out of the endpoints Wolverine itself knows about, use `AutoPurgeOnStartup()` instead.
:::

If you would rather clean up out of band -- say, once per test run rather than once per host -- call the standalone helper. It
takes the *management* connection string, and defaults to the standard emulator one:

```cs
// Same destructive semantics: this drops every queue and topic in the namespace
await AzureServiceBusEmulatorExtensions.DeleteAllAzureServiceBusObjectsAsync();
```

## Writing Integration Tests

Integration tests against the emulator are just normal Wolverine tests:

```cs
public class when_sending_messages : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusEmulator()
                    .AutoProvision()
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
Use `.IncludeExternalTransports()` on the tracked session so Wolverine waits for messages that travel through Azure Service Bus
rather than only tracking in-memory activity.
:::

## Disabling Parallel Test Execution

Because the emulator is a shared resource, tests that create and tear down queues or topics can interfere with each other when run
in parallel. Wolverine's own test suite disables parallel execution for its Azure Service Bus tests:

```cs
// Add to a file like NoParallelization.cs in your test project
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
```
