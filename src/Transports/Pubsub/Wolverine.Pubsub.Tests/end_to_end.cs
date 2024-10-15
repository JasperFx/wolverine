using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Pubsub.Internal;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class end_to_end : IAsyncLifetime {
    private IHost _host = default!;

    public async Task InitializeAsync() {
        DotNetEnv.Env.Load(".env.test");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => {
                opts.UsePubsubTesting().AutoProvision();
                opts.ListenToPubsubTopic("send_and_receive");
                opts.PublishMessage<AsbMessage1>().ToPubsubTopic("send_and_receive");
            }).StartAsync();
    }

    public async Task DisposeAsync() {
        await _host.StopAsync();
    }

    [Fact]
    public void builds_system_endpoints_by_default() {
        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<PubsubTransport>();
        var endpoints = transport
            .Endpoints()
            .Where(x => x.Role == EndpointRole.System);
        var topics = endpoints.OfType<PubsubTopic>().ToArray();
        var subscriptions = endpoints.OfType<PubsubSubscription>().ToArray();

        topics.ShouldContain(x => x.Name.TopicId.StartsWith(PubsubTransport.ResponseEndpointName));
        subscriptions.ShouldContain(x => x.Name.SubscriptionId.StartsWith(PubsubTransport.ResponseEndpointName));
    }

    [Fact]
    public async Task disable_system_endpoints() {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => {
                opts.UsePubsubTesting()
                    .AutoProvision()
                    .SystemEndpointsAreEnabled(false);
                opts.ListenToPubsubTopic("send_and_receive");
                opts.PublishAllMessages().ToPubsubTopic("send_and_receive");
            }).StartAsync();
        var transport = host.GetRuntime().Options.Transports.GetOrCreate<PubsubTransport>();
        var endpoints = transport
            .Endpoints()
            .Where(x => x.Role == EndpointRole.System)
            .OfType<PubsubEndpoint>().ToArray();

        endpoints.Any().ShouldBeFalse();
    }

    [Fact]
    public async Task send_and_receive_a_single_message() {
        var message = new AsbMessage1("Josh Allen");
        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(5.Minutes())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<AsbMessage1>().Name.ShouldBe(message.Name);
    }
}

public record AsbMessage1(string Name);
