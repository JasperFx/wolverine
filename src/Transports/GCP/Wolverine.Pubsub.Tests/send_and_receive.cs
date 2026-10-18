using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class send_and_receive : IAsyncLifetime
{
    private IHost _host = default!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts
                    .UsePubsubTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts
                    .PublishMessage<TestPubsubMessage>()
                    .ToPubsubTopic("send_and_receive");

                opts.ListenToPubsubTopic("send_and_receive");
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void system_endpoints_disabled_by_default()
    {
        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<PubsubTransport>();
        var endpoints = transport.Endpoints()
            .Where(x => x.Role == EndpointRole.System)
            .OfType<PubsubEndpoint>().ToArray();

        endpoints.Any().ShouldBeFalse();
    }

    [Fact]
    public async Task builds_system_endpoints()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableSystemEndpoints();

                opts.ListenToPubsubTopic("send_and_receive");

                opts
                    .PublishAllMessages()
                    .ToPubsubTopic("send_and_receive");
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        var transport = host.GetRuntime().Options.Transports.GetOrCreate<PubsubTransport>();
        var endpoints = transport
            .Endpoints()
            .Where(x => x.Role == EndpointRole.System)
            .OfType<PubsubEndpoint>().ToArray();

        endpoints.ShouldContain(x =>
            x.Server.Topic.Name.TopicId.StartsWith(PubsubTransport.ResponseName) &&
            x.Server.Subscription.Name.SubscriptionId.StartsWith(PubsubTransport.ResponseName)
        );
    }

    [Fact]
    public async Task send_and_receive_a_single_message()
    {
        var message = new TestPubsubMessage("Josh Allen");
        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(1.Minutes())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<TestPubsubMessage>().Name.ShouldBe(message.Name);
    }

    [Fact]
    public async Task send_and_receive_many_messages()
    {
        Func<IMessageBus, Task> sending = async bus =>
        {
            for (var i = 0; i < 10; i++)
            {
                await bus.PublishAsync(new TestPubsubMessage(Guid.NewGuid().ToString()));
            }
        };

        await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(5.Minutes())
            .ExecuteAndWaitAsync(sending);
    }
}

public record TestPubsubMessage(string Name);

public static class TestPubsubMessageHandler
{
    public static void Handle(TestPubsubMessage message)
    {
    }
}

public class send_and_receive_with_cloudevents : IAsyncLifetime
{
    private IHost _host = default!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts
                    .UsePubsubTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts
                    .PublishMessage<TestPubsubMessage>()
                    .ToPubsubTopic("cloudevents")
                    .InteropWithCloudEvents();

                opts.ListenToPubsubTopic("cloudevents").InteropWithCloudEvents();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
    
    [Fact]
    public async Task send_and_receive_a_single_message()
    {
        var message = new TestPubsubMessage("Josh Allen");
        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(1.Minutes())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<TestPubsubMessage>().Name.ShouldBe(message.Name);
    }

    [Fact]
    public async Task custom_headers_survive_a_cloud_events_endpoint()
    {
        // GH-5080
        var options = new DeliveryOptions();
        options.Headers["x-user"] = "alice";
        options.Headers["x-country"] = "ca";

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(1.Minutes())
            .SendMessageAndWaitAsync(new TestPubsubMessage("Josh Allen"), options);

        var envelope = session.Received.SingleEnvelope<TestPubsubMessage>();
        envelope.Headers["x-user"].ShouldBe("alice");
        envelope.Headers["x-country"].ShouldBe("ca");
    }

}