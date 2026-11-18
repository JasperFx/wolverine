using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// AutoPurgeOnStartup skips the purge for a queue or subscription that the same startup just created, because a new
/// entity is empty and purging an empty one still waits a second on the receive. A queue that was already there, with
/// messages in it, is still purged
/// </summary>
public class auto_purge_on_startup : IAsyncLifetime
{
    private const string QueueName = "auto-purge-existing";
    private IHost? _host;

    public ValueTask InitializeAsync()
    {
        // The suite deletes everything in the emulator the first time a process calls UseAzureServiceBusTesting(). Do that
        // now, before the queue below is created, or the host's own call would delete the queue and its message and the
        // test would pass without anything being purged
        new WolverineOptions().UseAzureServiceBusTesting();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public async Task a_queue_that_already_exists_is_still_purged()
    {
        var ct = TestContext.Current.CancellationToken;

        var admin = new ServiceBusAdministrationClient(Servers.AzureServiceBusManagementConnectionString);
        if (!(await admin.QueueExistsAsync(QueueName, ct)).Value)
        {
            await admin.CreateQueueAsync(QueueName, ct);
        }

        await using var client = new ServiceBusClient(Servers.AzureServiceBusConnectionString);
        await using (var sender = client.CreateSender(QueueName))
        {
            await sender.SendMessageAsync(new ServiceBusMessage("left over from an earlier run"), ct);
        }

        // A new receiver each time: a receiver's next peek carries on after the last message it peeked
        async Task<int> messagesInTheQueue()
        {
            await using var receiver = client.CreateReceiver(QueueName);
            return (await receiver.PeekMessagesAsync(10, cancellationToken: ct)).Count;
        }

        (await messagesInTheQueue()).ShouldBe(1);

        // Only publishes to the queue, so nothing but the purge can take the message off it
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting().AutoProvision().AutoPurgeOnStartup();
                opts.Discovery.DisableConventionalDiscovery();
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<AutoPurgeMessage>().ToAzureServiceBusQueue(QueueName);
            }).StartAsync(ct);

        (await messagesInTheQueue()).ShouldBe(0);
    }
}

public record AutoPurgeMessage;
