using Azure.Messaging.ServiceBus;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

// GH-3533: pinning a session-enabled listener to specific session identifiers turns the session id
// into a broker-enforced routing key on a shared queue, so a listener pinned to "A" never sees the
// messages meant for "B".
[Trait("Category", "Flaky")]
public class session_id_pinning : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("shared-pinned")

                    // Only ever lock the "A" session on this shared queue
                    .RequireSessionsWithOnlyTheseIdentifiers("A")
                    .Sequential();

                opts.PublishMessage<PinnedMessage>().ToAzureServiceBusQueue("shared-pinned");
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public async Task pinned_listener_only_receives_its_own_session()
    {
        await using var client = new ServiceBusClient(Servers.AzureServiceBusConnectionString);

        // Seed a message destined for session "B" directly onto the shared queue, bypassing Wolverine
        var sender = client.CreateSender("shared-pinned");
        await sender.SendMessageAsync(new ServiceBusMessage("not for A")
        {
            SessionId = "B",
            MessageId = Guid.NewGuid().ToString()
        });

        // Drive three "A" messages through Wolverine and confirm ONLY those are received
        Func<IMessageContext, Task> sendAll = async bus =>
        {
            await bus.SendAsync(new PinnedMessage("A-1"), new DeliveryOptions { GroupId = "A" });
            await bus.SendAsync(new PinnedMessage("A-2"), new DeliveryOptions { GroupId = "A" });
            await bus.SendAsync(new PinnedMessage("A-3"), new DeliveryOptions { GroupId = "A" });
        };

        var tracked = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(sendAll);

        // Every "A" message was delivered here (order is a separate FIFO concern), and nothing else
        tracked.Received.MessagesOf<PinnedMessage>().Select(x => x.Name).OrderBy(x => x)
            .ShouldBe(["A-1", "A-2", "A-3"]);

        // The "B" session message must still be sitting on the shared queue, never delivered to the
        // A-pinned listener.
        await using var sessionReceiver = await client.AcceptSessionAsync("shared-pinned", "B");
        var leftover = await sessionReceiver.ReceiveMessageAsync(5.Seconds());
        leftover.ShouldNotBeNull();
        leftover.SessionId.ShouldBe("B");
    }
}

public record PinnedMessage(string Name);

public static class PinnedMessageHandler
{
    public static void Handle(PinnedMessage message)
    {
        // no-op; tracking observes receipt
    }
}
