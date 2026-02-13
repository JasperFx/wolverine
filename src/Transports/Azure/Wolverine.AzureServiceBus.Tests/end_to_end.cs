using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class end_to_end : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        #region sample_using_azure_service_bus_session_identifiers

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("send_and_receive");
                opts.PublishMessage<AsbMessage1>().ToAzureServiceBusQueue("send_and_receive");

                opts.ListenToAzureServiceBusQueue("fifo1")

                    // Require session identifiers with this queue
                    .RequireSessions()

                    // This controls the Wolverine handling to force it to process
                    // messages sequentially
                    .Sequential();

                opts.PublishMessage<AsbMessage2>()
                    .ToAzureServiceBusQueue("fifo1");

                opts.PublishMessage<AsbMessage3>().ToAzureServiceBusTopic("asb3").SendInline();
                opts.ListenToAzureServiceBusSubscription("asb3")
                    .FromTopic("asb3")

                    // Require sessions on this subscription
                    .RequireSessions(1)

                    .ProcessInline();

                opts.PublishMessage<AsbMessage4>().ToAzureServiceBusTopic("asb4").BufferedInMemory();
                opts.ListenToAzureServiceBusSubscription("asb4")
                    .FromTopic("asb4")

                    // Require sessions on this subscription
                    .RequireSessions(1)

                    .ProcessInline();
            }).StartAsync();

        #endregion
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public void builds_response_and_retry_queue_by_default()
    {
        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<AzureServiceBusTransport>();
        var endpoints = transport
            .Endpoints()
            .Where(x => x.Role == EndpointRole.System)
            .OfType<AzureServiceBusQueue>().ToArray();

        endpoints.ShouldContain(x => x.QueueName.StartsWith("wolverine.response."));
        endpoints.ShouldContain(x => x.QueueName.StartsWith("wolverine.retries."));
    }

    [Fact]
    public async Task disable_system_queues()
    {
        #region sample_disable_system_queues_in_azure_service_bus

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup()
                    .SystemQueuesAreEnabled(false);

                opts.ListenToAzureServiceBusQueue("send_and_receive");

                opts.PublishAllMessages().ToAzureServiceBusQueue("send_and_receive");
            }).StartAsync();

        #endregion

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<AzureServiceBusTransport>();

        var endpoints = transport
            .Endpoints()
            .Where(x => x.Role == EndpointRole.System)
            .OfType<AzureServiceBusQueue>().ToArray();

        endpoints.Any().ShouldBeFalse();

    }

    [Fact]
    public async Task send_and_receive_a_single_message()
    {
        var message = new AsbMessage1("Josh Allen");

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(5.Minutes())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<AsbMessage1>()
            .Name.ShouldBe(message.Name);
    }

    [Fact]
    public async Task send_and_receive_multiple_messages_to_queue_with_session_identifier()
    {
        Func<IMessageContext, Task> sendMany = async c =>
        {
            await c.SendAsync(new AsbMessage2("One"), new DeliveryOptions { GroupId = "1" });
            await c.SendAsync(new AsbMessage2("Two"), new DeliveryOptions { GroupId = "1" });
            await c.SendAsync(new AsbMessage2("Three"), new DeliveryOptions { GroupId = "1" });
        };

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(sendMany);

        var names = session.Received.MessagesOf<AsbMessage2>().Select(x => x.Name).ToArray();
        names
            .ShouldBe(["One", "Two", "Three"]);
    }

    [Fact]
    public async Task send_and_receive_multiple_messages_to_subscription_with_session_identifier()
    {
        Func<IMessageContext, Task> sendMany = async bus =>
        {
            #region sample_sending_with_session_identifier

            // bus is an IMessageBus
            await bus.SendAsync(new AsbMessage3("Red"), new DeliveryOptions { GroupId = "2" });
            await bus.SendAsync(new AsbMessage3("Green"), new DeliveryOptions { GroupId = "2" });
            await bus.SendAsync(new AsbMessage3("Refactor"), new DeliveryOptions { GroupId = "2" });

            #endregion
        };

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(sendMany);

        session.Received.MessagesOf<AsbMessage3>().Select(x => x.Name)
            .ShouldBe(new string[]{"Red", "Green", "Refactor"});
    }

    [Fact]
    public async Task split_messages_with_different_sessionids_into_separate_batches()
    {
        Func<IMessageContext, Task> sendMany = async bus =>
        {
            await bus.SendAsync(new AsbMessage4("Dummy 1.1"), new DeliveryOptions { GroupId = "1" });
            await bus.SendAsync(new AsbMessage4("Dummy 1.2"), new DeliveryOptions { GroupId = "1" });
            await bus.SendAsync(new AsbMessage4("Dummy 2.1"), new DeliveryOptions { GroupId = "2" });
            await bus.SendAsync(new AsbMessage4("Dummy 3.1"), new DeliveryOptions { GroupId = "3" });
            await bus.SendAsync(new AsbMessage4("Dummy 4.1"), new DeliveryOptions { GroupId = "4" });
            await bus.SendAsync(new AsbMessage4("Dummy 4.2"), new DeliveryOptions { GroupId = "4" });
        };

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(sendMany);

        // Verify that all messages were received and processed in order inside the session
        var names = session.Received.MessagesOf<AsbMessage4>().Select(x => x.Name).ToList();
        names.Count.ShouldBe(6);
        names.Where(x => x.StartsWith("Dummy 1")).ShouldBe(["Dummy 1.1", "Dummy 1.2"]);
        names.Where(x => x.StartsWith("Dummy 2")).ShouldBe(["Dummy 2.1"]);
        names.Where(x => x.StartsWith("Dummy 3")).ShouldBe(["Dummy 3.1"]);
        names.Where(x => x.StartsWith("Dummy 4")).ShouldBe(["Dummy 4.1", "Dummy 4.2"]);
    }
}

public record AsbMessage1(string Name);
public record AsbMessage2(string Name);
public record AsbMessage3(string Name);
public record AsbMessage4(string Name);

public static class AsbMessageHandler
{
    public static void Handle(AsbMessage1 message)
    {
        // nothing
    }

    public static void Handle(AsbMessage2 message)
    {
        // nothing
    }

    public static void Handle(AsbMessage3 message)
    {
        // nothing
    }

    public static void Handle(AsbMessage4 message)
    {
        // nothing
    }
}