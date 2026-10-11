using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.MQTT.Tests;

/// <summary>
/// The MQTT twin of GH-4930. PublishMessagesToMqttTopic registers the same TopicRouting source as the NATS
/// dynamic subjects, so an explicit local queue rule for the same message type was never consulted. The
/// additive: true overload lets every other routing rule apply as well; the original overload is unchanged.
/// </summary>
[Collection("acceptance")]
public class additive_topic_routing : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private LocalMqttBroker _broker = null!;
    private int _port;

    public additive_topic_routing(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        _port = PortFinder.GetAvailablePort();
        _broker = new LocalMqttBroker(_port) { Logger = new XUnitLogger(_output, "MQTT") };
        await _broker.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.StopAsync();
    }

    [Fact]
    public async Task publishes_to_the_topic_and_to_the_local_queue()
    {
        var port = _port;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType(typeof(AdditiveMqttCommandHandler));

                #region sample_publish_to_mqtt_topic_and_local_queue

                opts.UseMqttWithLocalBroker(port);

                // Every AdditiveMqttCommand goes to an MQTT topic computed from the message...
                opts.PublishMessagesToMqttTopic<AdditiveMqttCommand>(m => $"commands/{m.Region}", additive: true);

                // ...and because that routing is additive, to the local "commands" queue at the same time
                opts.PublishMessage<AdditiveMqttCommand>().ToLocalQueue("commands");

                #endregion
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        _output.WriteLine(runtime.ExplainRoutingFor(typeof(AdditiveMqttCommand)).ToText());

        var routes = runtime.RoutingFor(typeof(AdditiveMqttCommand)).Routes
            .Select(x => x.Describe().Endpoint.ToString())
            .ToArray();

        routes.ShouldContain(x => x.StartsWith("local://commands"));
        routes.ShouldContain(x => x.StartsWith("mqtt://"));

        // Nothing in this test listens on the topic, so the session waits on the local handler only
        var session = await host
            .TrackActivity()
            .Timeout(15.Seconds())
            .PublishMessageAndWaitAsync(new AdditiveMqttCommand("west"));

        session.Executed.SingleMessage<AdditiveMqttCommand>().Region.ShouldBe("west");
        session.Sent.Envelopes().ShouldContain(x => x.Destination!.Scheme == "mqtt" && x.TopicName == "commands/west");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_original_overload_still_routes_only_to_the_topic()
    {
        var port = _port;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType(typeof(AdditiveMqttCommandHandler));
                opts.UseMqttWithLocalBroker(port);

                opts.PublishMessagesToMqttTopic<AdditiveMqttCommand>(m => $"commands/{m.Region}");
                opts.PublishMessage<AdditiveMqttCommand>().ToLocalQueue("commands");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        runtime.RoutingFor(typeof(AdditiveMqttCommand)).Routes
            .Select(x => x.Describe().Endpoint.ToString())
            .ShouldHaveSingleItem()
            .ShouldStartWith("mqtt://");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record AdditiveMqttCommand(string Region);

[WolverineIgnore]
public static class AdditiveMqttCommandHandler
{
    public static void Handle(AdditiveMqttCommand command)
    {
    }
}
