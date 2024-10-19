using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Pubsub.Internal;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class send_and_receive : IAsyncLifetime {
    private IHost _host = default!;

    public async Task InitializeAsync() {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");
        Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => {
                opts.UsePubsubTesting().AutoProvision();
                opts.ListenToPubsubTopic("send_and_receive", x => x.Mapper = new TestPubsubEnvelopeMapper(x));
                opts.PublishMessage<PubsubMessage1>().ToPubsubTopic("send_and_receive");
            }).StartAsync();
    }

    public async Task DisposeAsync() {
        await _host.StopAsync();
    }

    [Fact]
    public void system_endpoints_disabled_by_default() {
        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<PubsubTransport>();
        var endpoints = transport
            .Endpoints()
            .Where(x => x.Role == EndpointRole.System)
            .OfType<PubsubEndpoint>().ToArray();

        endpoints.Any().ShouldBeFalse();
    }

    [Fact]
    public async Task builds_system_endpoints() {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => {
                opts.UsePubsubTesting()
                    .AutoProvision()
                    .SystemEndpointsAreEnabled(true);
                opts.ListenToPubsubTopic("send_and_receive");
                opts.PublishAllMessages().ToPubsubTopic("send_and_receive");
            }).StartAsync();
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
    public async Task send_and_receive_a_single_message() {
        var message = new PubsubMessage1("Josh Allen");
        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(1.Minutes())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<PubsubMessage1>().Name.ShouldBe(message.Name);
    }

    [Fact]
    public async Task send_and_receive_many_messages() {
        Func<IMessageBus, Task> sending = async bus => {
            for (int i = 0; i < 100; i++)
                await bus.PublishAsync(new PubsubMessage1(Guid.NewGuid().ToString()));
        };

        await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(5.Minutes())
            .ExecuteAndWaitAsync(sending);
    }
}

public record PubsubMessage1(string Name);
