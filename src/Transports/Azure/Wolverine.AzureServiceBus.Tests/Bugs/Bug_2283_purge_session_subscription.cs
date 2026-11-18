using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Bugs;

public class Bug_2283_purge_session_subscription : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        // This should not throw even though the subscription has sessions enabled
        // and AutoPurgeOnStartup is set. Before the fix, PurgeAsync on a session-enabled
        // subscription used CreateReceiver instead of AcceptNextSessionAsync, which fails.
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<Bug2283Message>()
                    .ToAzureServiceBusTopic("bug2283")
                    .SendInline();

                opts.ListenToAzureServiceBusSubscription("bug2283sub")
                    .FromTopic("bug2283")
                    .RequireSessions(1)
                    .ProcessInline();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public async Task can_start_with_auto_purge_and_session_enabled_subscription()
    {
        // If we got here, the host started successfully with AutoPurgeOnStartup
        // and a session-enabled subscription without throwing.
        // Now verify we can also send and receive messages through it.
        Func<IMessageContext, Task> sendMany = async bus =>
        {
            await bus.SendAsync(new Bug2283Message("First"), new DeliveryOptions { GroupId = "session1" });
            await bus.SendAsync(new Bug2283Message("Second"), new DeliveryOptions { GroupId = "session1" });
            await bus.SendAsync(new Bug2283Message("Third"), new DeliveryOptions { GroupId = "session1" });
        };

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(sendMany);

        session.Received.MessagesOf<Bug2283Message>().Select(x => x.Name)
            .ShouldBe(["First", "Second", "Third"]);
    }

    [Fact]
    public async Task can_purge_existing_messages_from_session_subscription()
    {
        var ct = TestContext.Current.CancellationToken;

        // The first host stops listening, so the messages below really sit in the subscription, and so nothing but the
        // second host can take a session. A first host still listening raced the second for the new session at the end,
        // and when it won, the second host's tracked session never saw its own message
        await _host.StopAsync(ct);

        await using var client = new ServiceBusClient(Servers.AzureServiceBusConnectionString);
        await using (var sender = client.CreateSender("bug2283"))
        {
            await sender.SendMessageAsync(new ServiceBusMessage("Pre1") { SessionId = "purge-test" }, ct);
            await sender.SendMessageAsync(new ServiceBusMessage("Pre2") { SessionId = "purge-test" }, ct);
        }

        var admin = new ServiceBusAdministrationClient(Servers.AzureServiceBusManagementConnectionString);
        // The emulator's message counts trail a send or a settlement by a moment, and it reports a session subscription's
        // messages in TotalMessageCount only (ActiveMessageCount stays 0). Nothing dead letters here, so that is the
        // count of messages waiting
        async Task<long> waitForMessagesInTheSubscription(long expected)
        {
            long count = -1;
            for (var i = 0; i < 50 && count != expected; i++)
            {
                if (i > 0) await Task.Delay(200, ct);
                count = (await admin.GetSubscriptionRuntimePropertiesAsync("bug2283", "bug2283sub", ct)).Value.TotalMessageCount;
            }

            return count;
        }

        (await waitForMessagesInTheSubscription(2)).ShouldBe(2);

        // Now start a second host with the same subscription + AutoPurgeOnStartup.
        // This validates that purge works when there ARE messages in a session-enabled subscription.
        using var host2 = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<Bug2283Message>()
                    .ToAzureServiceBusTopic("bug2283")
                    .SendInline();

                opts.ListenToAzureServiceBusSubscription("bug2283sub")
                    .FromTopic("bug2283")
                    .RequireSessions(1)
                    .ProcessInline();
            }).StartAsync(cancellationToken: ct);

        (await waitForMessagesInTheSubscription(0)).ShouldBe(0);

        // Send new messages through host2 and verify only the new ones arrive
        Func<IMessageContext, Task> sendNew = async bus =>
        {
            await bus.SendAsync(new Bug2283Message("New1"), new DeliveryOptions { GroupId = "purge-test-2" });
        };

        var session = await host2.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(sendNew);

        session.Received.MessagesOf<Bug2283Message>().Select(x => x.Name).ShouldBe(["New1"]);

        await host2.StopAsync(ct);
    }
}

public record Bug2283Message(string Name);

public static class Bug2283Handler
{
    public static void Handle(Bug2283Message message)
    {
        // nothing
    }
}
