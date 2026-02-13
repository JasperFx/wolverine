# Azure Service Bus Tests

## Prerequisites

These tests run against the [Azure Service Bus Emulator](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator), which is started via `docker-compose` alongside the other Wolverine test infrastructure (PostgreSQL, RabbitMQ, Kafka, etc.).

```bash
docker compose up -d
```

This starts two containers for the emulator:

| Service | Image | Purpose | Host Port |
|---------|-------|---------|-----------|
| `asb-sql` | `mcr.microsoft.com/azure-sql-edge` | Backing database for the emulator | (internal only) |
| `asb-emulator` | `mcr.microsoft.com/azure-messaging/servicebus-emulator:latest` | The emulator itself | **5673** (AMQP), **5300** (HTTP management) |

Port 5673 is used instead of the default 5672 to avoid conflicting with the RabbitMQ container.

## Dual Connection Strings

The emulator exposes two separate endpoints:

- **AMQP (port 5673)** -- Used by `ServiceBusClient` for sending and receiving messages
- **HTTP (port 5300)** -- Used by `ServiceBusAdministrationClient` for managing queues, topics, and subscriptions

Because of this, `AzureServiceBusTransport` has a `ManagementConnectionString` property that allows the administration client to target the HTTP endpoint independently. The test helper `UseAzureServiceBusTesting()` configures both automatically.

## Emulator Configuration

The emulator config lives at `docker/asb/Config.json` and defines a single namespace (`sbemulatorns`). Queues, topics, and subscriptions are created dynamically by the tests via `AutoProvision()`.

## Emulator Limitations

- **50 entity limit** -- The emulator supports a maximum of 50 queues, topics, and subscriptions combined per namespace. Each test class cleans up all entities in its `DisposeAsync` / `AfterDisposeAsync` to stay under this limit.
- **No partitioned entities** -- `EnablePartitioning` is not supported.
- **One namespace per instance** -- Multi-namespace scenarios would require separate emulator containers.

## Running the Tests

```bash
# Single target framework for faster runs
dotnet test src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Wolverine.AzureServiceBus.Tests.csproj \
  --framework net9.0

# Run a specific test class
dotnet test src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Wolverine.AzureServiceBus.Tests.csproj \
  --framework net9.0 \
  --filter "FullyQualifiedName~end_to_end"
```

## Test Cleanup Pattern

Tests run serially (see `NoParallelization.cs`). To stay within the 50 entity limit, every test class deletes all emulator objects after it finishes:

- **Compliance fixtures** override `AfterDisposeAsync()` in the base `TransportComplianceFixture`
- **Leader election tests** override `AfterDisposeAsync()` in `LeadershipElectionCompliance`
- **Other test classes** implement `IAsyncLifetime` and call `AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync()` in `DisposeAsync`

## Writing New Tests

Use the `UseAzureServiceBusTesting()` extension method to configure a Wolverine host for the emulator:

```csharp
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBusTesting()
            .AutoProvision()
            .AutoPurgeOnStartup();

        opts.ListenToAzureServiceBusQueue("my-queue");
        opts.PublishMessage<MyMessage>().ToAzureServiceBusQueue("my-queue");
    }).StartAsync();
```

Make sure your test class cleans up emulator objects on disposal to avoid hitting the 50 entity limit for subsequent test classes:

```csharp
public class my_tests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();

    // tests...
}
```

Avoid defining message record types as nested classes inside test classes. C# nested types have `+` in their CLR name (e.g. `my_tests+MyMessage`), and Azure Service Bus rejects `+` in entity names.
