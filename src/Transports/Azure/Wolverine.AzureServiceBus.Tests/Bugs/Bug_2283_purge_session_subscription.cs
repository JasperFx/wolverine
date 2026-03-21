using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Bugs;

[Trait("Category", "Flaky")]
public class Bug_2283_purge_session_subscription : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
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

    public async Task DisposeAsync()
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
        // First, send some messages that will sit in the subscription
        Func<IMessageContext, Task> sendMessages = async bus =>
        {
            await bus.SendAsync(new Bug2283Message("Pre1"), new DeliveryOptions { GroupId = "purge-test" });
            await bus.SendAsync(new Bug2283Message("Pre2"), new DeliveryOptions { GroupId = "purge-test" });
        };

        await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(sendMessages);

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
            }).StartAsync();

        // Send new messages through host2 and verify only the new ones arrive
        Func<IMessageContext, Task> sendNew = async bus =>
        {
            await bus.SendAsync(new Bug2283Message("New1"), new DeliveryOptions { GroupId = "purge-test-2" });
        };

        var session = await host2.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(sendNew);

        var received = session.Received.MessagesOf<Bug2283Message>().Select(x => x.Name).ToArray();
        received.ShouldContain("New1");

        await host2.StopAsync();
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
