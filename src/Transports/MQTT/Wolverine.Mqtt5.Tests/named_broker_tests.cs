using System.Buffers;
using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

/// <summary>
/// Registration-level coverage for named MQTT brokers that needs no running broker, so it always runs in CI.
/// A named broker is a second, independent <see cref="MqttTransport"/> whose <c>Protocol</c> (and therefore its
/// endpoints' URI scheme) is the broker name, so its endpoints never collide with the default <c>mqtt://</c>
/// broker.
/// </summary>
public class NamedMqttBrokerRegistrationTests
{
    private readonly BrokerName theName = new("secondary");

    [Fact]
    public void adds_a_distinct_transport_instance_per_broker()
    {
        var options = new WolverineOptions();
        options.UseMqttWithLocalBroker(1883);
        options.AddNamedMqttBroker(theName, builder =>
            builder.WithClientOptions(o => o.WithTcpServer("127.0.0.1", 1884)));

        var transports = options.Transports.OfType<MqttTransport>().ToList();
        transports.Count.ShouldBe(2);
        transports.Select(x => x.Protocol).OrderBy(x => x).ShouldBe(["mqtt", "secondary"]);
    }

    [Fact]
    public void named_broker_endpoints_use_the_broker_name_as_their_uri_scheme()
    {
        var options = new WolverineOptions();
        options.UseMqttWithLocalBroker(1883);
        options.AddNamedMqttBroker(theName, builder =>
            builder.WithClientOptions(o => o.WithTcpServer("127.0.0.1", 1884)));

        var named = options.Transports.OfType<MqttTransport>().Single(x => x.Protocol == "secondary");
        named.Topics["incoming/one"].Uri.ShouldBe(new Uri("secondary://topic/incoming/one"));

        // ...and the default broker keeps the canonical mqtt:// scheme.
        var @default = options.Transports.OfType<MqttTransport>().Single(x => x.Protocol == "mqtt");
        @default.Topics["incoming/one"].Uri.ShouldBe(new Uri("mqtt://topic/incoming/one"));
    }

    [Fact]
    public void listen_on_named_broker_registers_the_listener_on_the_named_transport_only()
    {
        var options = new WolverineOptions();
        options.UseMqttWithLocalBroker(1883);
        options.AddNamedMqttBroker(theName, builder =>
            builder.WithClientOptions(o => o.WithTcpServer("127.0.0.1", 1884)));

        options.ListenToMqttTopicOnNamedBroker(theName, "incoming/one");

        var named = options.Transports.OfType<MqttTransport>().Single(x => x.Protocol == "secondary");
        named.Topics["incoming/one"].IsListener.ShouldBeTrue();
    }
}

/// <summary>
/// End-to-end coverage that a named MQTT broker actually talks to a <em>different</em> broker than the default
/// one. Two in-process <see cref="LocalMqttBroker"/> instances stand in for two brokers (A = default, B = named)
/// on two free ports — no Docker needed. Proves:
/// <list type="bullet">
/// <item>a message published on the named broker lands on <b>broker B</b> and not broker A,</item>
/// <item>a default publish lands on <b>broker A</b> and not broker B,</item>
/// <item>a message published <b>and consumed</b> on the named broker round-trips and arrives stamped with the
/// broker's own URI scheme (the receive pipeline sets <c>Destination</c> from the listener endpoint's URI), and</item>
/// <item>a full request/reply round-trips over the named broker, proving the reply is routed back to broker B
/// rather than the default broker A.</item>
/// </list>
/// </summary>
[Collection("acceptance")]
public class named_broker_tests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly BrokerName theName = new("secondary");
    private LocalMqttBroker _brokerA = null!;
    private LocalMqttBroker _brokerB = null!;
    private int _portA;
    private int _portB;

    public named_broker_tests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _portA = PortFinder.GetAvailablePort();
        _portB = PortFinder.GetAvailablePort();

        _brokerA = new LocalMqttBroker(_portA) { Logger = new XUnitLogger(_output, "MQTT-A") };
        _brokerB = new LocalMqttBroker(_portB) { Logger = new XUnitLogger(_output, "MQTT-B") };

        await _brokerA.StartAsync();
        await _brokerB.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _brokerA.DisposeAsync();
        await _brokerB.DisposeAsync();
    }

    [Fact]
    public async Task named_broker_send_lands_on_the_named_broker()
    {
        var topic = $"named/{Guid.NewGuid():N}";

        await using var subOnB = await RawMqttSubscriber.StartAsync(_portB, topic);
        await using var subOnA = await RawMqttSubscriber.StartAsync(_portA, topic);

        using var host = await buildSenderAsync(topic, useNamedBroker: true);

        await host.MessageBus().SendAsync(new NamedColor("on-named-broker"));

        // Landed on broker B (the named broker's connection)...
        (await subOnB.WaitForMessageAsync(15.Seconds())).ShouldBeTrue();
        // ...and NOT on the default broker A.
        (await subOnA.WaitForMessageAsync(2.Seconds())).ShouldBeFalse();
    }

    [Fact]
    public async Task default_broker_send_lands_on_the_default_broker()
    {
        var topic = $"named/{Guid.NewGuid():N}";

        await using var subOnA = await RawMqttSubscriber.StartAsync(_portA, topic);
        await using var subOnB = await RawMqttSubscriber.StartAsync(_portB, topic);

        using var host = await buildSenderAsync(topic, useNamedBroker: false);

        await host.MessageBus().SendAsync(new NamedColor("on-default-broker"));

        (await subOnA.WaitForMessageAsync(15.Seconds())).ShouldBeTrue();
        (await subOnB.WaitForMessageAsync(2.Seconds())).ShouldBeFalse();
    }

    [Fact]
    public async Task round_trips_a_message_over_the_named_broker()
    {
        var topic = $"named/{Guid.NewGuid():N}";

        // A single host both publishes and listens on the named broker (broker B). On receipt the pipeline stamps
        // Destination from the listener endpoint's URI, so the consumed envelope carries the named broker's
        // "secondary" scheme rather than the default "mqtt".
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(_portA);
                opts.AddNamedMqttBroker(theName, builder =>
                    builder.WithClientOptions(o => o.WithTcpServer("127.0.0.1", _portB)));

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<NamedColor>().ToMqttTopicOnNamedBroker(theName, topic).SendInline();
                opts.ListenToMqttTopicOnNamedBroker(theName, topic);
            }).StartAsync();

        var session = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<NamedColor>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new NamedColor("round-trip")));

        var received = session.Received.SingleEnvelope<NamedColor>();
        received.Message.ShouldBeOfType<NamedColor>().Color.ShouldBe("round-trip");
        received.Destination!.Scheme.ShouldBe("secondary");
    }

    [Fact]
    public async Task request_reply_round_trips_over_the_named_broker()
    {
        var topic = $"named/rr/{Guid.NewGuid():N}";

        // Request/reply entirely over the named broker (broker B). The reply is routed by the reply-uri's scheme,
        // which the pipeline corrects to the receiving endpoint's scheme ("secondary") — so the response travels
        // back over broker B, not the default broker A. If reply routing resolved to the default broker,
        // InvokeAndWaitAsync would time out.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(_portA);
                opts.AddNamedMqttBroker(theName, builder =>
                    builder.WithClientOptions(o => o.WithTcpServer("127.0.0.1", _portB)));

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<NamedPing>().ToMqttTopicOnNamedBroker(theName, topic);
                opts.ListenToMqttTopicOnNamedBroker(theName, topic);
            }).StartAsync();

        var (_, response) = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .InvokeAndWaitAsync<NamedPong>(new NamedPing("named-rr"));

        response.ShouldNotBeNull();
        response.Name.ShouldBe("named-rr");
    }

    private Task<IHost> buildSenderAsync(string topic, bool useNamedBroker)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(_portA);
                opts.AddNamedMqttBroker(theName, builder =>
                    builder.WithClientOptions(o => o.WithTcpServer("127.0.0.1", _portB)));

                opts.Policies.DisableConventionalLocalRouting();

                if (useNamedBroker)
                {
                    opts.PublishMessage<NamedColor>().ToMqttTopicOnNamedBroker(theName, topic).SendInline();
                }
                else
                {
                    opts.PublishMessage<NamedColor>().ToMqttTopic(topic).SendInline();
                }
            }).StartAsync();
    }
}

/// <summary>
/// A minimal raw MQTTnet subscriber used to assert which physical broker a Wolverine publish actually reached,
/// independent of Wolverine's own routing.
/// </summary>
internal class RawMqttSubscriber : IAsyncDisposable
{
    private readonly IManagedMqttClient _client;
    private readonly List<string> _messages = new();

    private RawMqttSubscriber(IManagedMqttClient client) => _client = client;

    public static async Task<RawMqttSubscriber> StartAsync(int port, string topic)
    {
        var client = new MqttClientFactory().CreateManagedMqttClient();
        var subscriber = new RawMqttSubscriber(client);

        client.ApplicationMessageReceivedAsync += e =>
        {
            lock (subscriber._messages)
            {
                subscriber._messages.Add(Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray()));
            }

            return Task.CompletedTask;
        };

        await client.StartAsync(new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(o => o.WithTcpServer("127.0.0.1", port)).Build());
        await client.SubscribeAsync(topic);

        return subscriber;
    }

    public async Task<bool> WaitForMessageAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_messages)
            {
                if (_messages.Count > 0) return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.StopAsync();
        _client.Dispose();
    }
}

public record NamedColor(string Color);

public static class NamedColorHandler
{
    public static void Handle(NamedColor message)
    {
        // no-op; presence lets Wolverine discover a handler so receive tests can track processing
    }
}

public record NamedPing(string Name);

public record NamedPong(string Name);

public class NamedPingHandler
{
    public NamedPong Handle(NamedPing ping) => new(ping.Name);
}
