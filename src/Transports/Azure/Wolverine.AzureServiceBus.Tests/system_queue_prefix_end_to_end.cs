using Azure.Messaging.ServiceBus.Administration;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

// Proves the prefixed system queues are actually provisioned and usable against the emulator, not
// just named correctly in memory: a host with a prefix still round trips a message, and the
// response, retry and dead letter queues exist in the namespace under the prefixed names.
public class system_queue_prefix_end_to_end : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PrefixedApp";

                // UseAzureServiceBusTesting() already turns on AutoProvision()
                opts.UseAzureServiceBusTesting()
                    .AutoPurgeOnStartup()
                    .SystemQueuePrefix("acme");

                opts.ListenToAzureServiceBusQueue("prefixed_send_and_receive");
                opts.PublishMessage<PrefixedAsbMessage>().ToAzureServiceBusQueue("prefixed_send_and_receive");
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public async Task send_and_receive_with_prefixed_system_queues()
    {
        var message = new PrefixedAsbMessage("Stefon Diggs");

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<PrefixedAsbMessage>()
            .Name.ShouldBe(message.Name);
    }

    [Fact]
    public async Task the_prefixed_system_queues_are_provisioned()
    {
        var ct = TestContext.Current.CancellationToken;

        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<AzureServiceBusTransport>();
        var systemQueues = transport
            .Endpoints()
            .Where(x => x.Role == EndpointRole.System)
            .OfType<AzureServiceBusQueue>()
            .ToArray();

        // The response queue carries the node identifier, so take the real name from the transport
        // rather than trying to reconstruct it here.
        var responseQueueName = systemQueues.Single(x => x.QueueName.Contains("wolverine.response")).QueueName;
        responseQueueName.ShouldStartWith("acme.wolverine.response.PrefixedApp.");

        var admin = new ServiceBusAdministrationClient(Servers.AzureServiceBusManagementConnectionString);

        (await admin.QueueExistsAsync(responseQueueName, ct)).Value.ShouldBeTrue();
        (await admin.QueueExistsAsync("acme.wolverine.retries.prefixedapp", ct)).Value.ShouldBeTrue();
        (await admin.QueueExistsAsync("acme.wolverine-dead-letter-queue", ct)).Value.ShouldBeTrue();

        // ...and nothing was provisioned under the unprefixed names
        (await admin.QueueExistsAsync("wolverine-dead-letter-queue", ct)).Value.ShouldBeFalse();
    }
}

public record PrefixedAsbMessage(string Name);

public static class PrefixedAsbMessageHandler
{
    public static void Handle(PrefixedAsbMessage message)
    {
        // nothing
    }
}
