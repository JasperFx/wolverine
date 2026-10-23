using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;
using Wolverine.Tracking;
using Wolverine.Util;

namespace Wolverine.MQTT.Tests;

/// <summary>
/// GH-5126: <c>UseMqttUsingNamedConnection</c> / <c>AddNamedMqttBrokerUsingNamedConnection</c> read the broker
/// connection from <c>ConnectionStrings:{name}</c> once the container is built. The CommunityToolkit Aspire
/// Mosquitto integration (<c>builder.AddMosquitto("mqtt")</c>) supplies <c>mqtt://host:port</c>.
/// </summary>
public class named_connection_5126
{
    private static readonly BrokerName theSecondary = new("secondary");

    private static Task<IHost> startStubbedAsync(Dictionary<string, string?> connectionStrings,
        Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(connectionStrings))
            .UseWolverine(opts =>
            {
                configure(opts);
                opts.StubAllExternalTransports();
            }).StartAsync();
    }

    [Fact]
    public async Task missing_named_connection_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await startStubbedAsync([], opts => opts.UseMqttUsingNamedConnection("mqtt"));
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["mqtt"]);
        ex.Message.ShouldContain("builder.AddMosquitto(\"mqtt\")");
    }

    [Fact]
    public async Task missing_named_connection_for_named_broker_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await startStubbedAsync([],
                opts => opts.AddNamedMqttBrokerUsingNamedConnection(theSecondary, "mqtt2"));
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["mqtt2"]);
        ex.Message.ShouldContain("builder.AddMosquitto(\"mqtt2\")");
    }

    [Theory]
    [InlineData("mqtt://mqtt-host:1884", "mqtt-host:1884")] // CommunityToolkit Aspire Mosquitto
    [InlineData("mqtt://mqtt-host", "mqtt-host:1883")]
    [InlineData("tcp://mqtt-host:1885", "mqtt-host:1885")]
    [InlineData("mqtt-host:1886", "mqtt-host:1886")]
    [InlineData("mqtt-host", "mqtt-host:1883")]
    [InlineData("mqtts://mqtt-host", "mqtt-host:8883")]
    [InlineData("ws://mqtt-host:8080/mqtt", "ws://mqtt-host:8080/mqtt")]
    public async Task applies_the_named_connection_to_the_default_broker(string value, string expected)
    {
        using var host = await startStubbedAsync(new() { ["ConnectionStrings:mqtt"] = value },
            opts => opts.UseMqttUsingNamedConnection("mqtt"));

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<MqttTransport>();
        transport.DescribeEndpoint().ShouldBe(expected);
    }

    [Theory]
    [InlineData("mqtt://mqtt-host:1883", false)]
    [InlineData("mqtts://mqtt-host:8883", true)]
    [InlineData("wss://mqtt-host:443/mqtt", true)]
    public async Task tls_follows_the_scheme(string value, bool useTls)
    {
        using var host = await startStubbedAsync(new() { ["ConnectionStrings:mqtt"] = value },
            opts => opts.UseMqttUsingNamedConnection("mqtt"));

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<MqttTransport>();
        transport.Options.ClientOptions.ChannelOptions.TlsOptions.UseTls.ShouldBe(useTls);
    }

    [Fact]
    public async Task credentials_in_the_uri_become_the_client_credentials()
    {
        using var host = await startStubbedAsync(
            new() { ["ConnectionStrings:mqtt"] = "mqtt://wolverine:s%40cret@mqtt-host:1883" },
            opts => opts.UseMqttUsingNamedConnection("mqtt"));

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<MqttTransport>();
        var clientOptions = transport.Options.ClientOptions;
        clientOptions.Credentials.GetUserName(clientOptions).ShouldBe("wolverine");
        System.Text.Encoding.UTF8.GetString(clientOptions.Credentials.GetPassword(clientOptions)).ShouldBe("s@cret");

        // ...and never leak into the diagnostic description
        transport.DescribeEndpoint().ShouldBe("mqtt-host:1883");
    }

    [Fact]
    public async Task applies_the_named_connection_to_a_named_broker()
    {
        using var host = await startStubbedAsync(new()
        {
            ["ConnectionStrings:mqtt"] = "mqtt://primary:1883",
            ["ConnectionStrings:mqtt2"] = "mqtt://secondary-host:1884"
        }, opts =>
        {
            opts.UseMqttUsingNamedConnection("mqtt");
            opts.AddNamedMqttBrokerUsingNamedConnection(theSecondary, "mqtt2");
        });

        var transports = host.GetRuntime().Options.Transports;
        transports.GetOrCreate<MqttTransport>().DescribeEndpoint().ShouldBe("primary:1883");
        transports.GetOrCreate<MqttTransport>(theSecondary).DescribeEndpoint().ShouldBe("secondary-host:1884");
    }

    [Fact]
    public async Task extra_client_configuration_is_applied_after_the_named_connection()
    {
        using var host = await startStubbedAsync(new() { ["ConnectionStrings:mqtt"] = "mqtt://mqtt-host:1883" },
            opts => opts.UseMqttUsingNamedConnection("mqtt", client => client.WithClientId("my-client")));

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<MqttTransport>();
        transport.Options.ClientOptions.ClientId.ShouldBe("my-client");
        transport.DescribeEndpoint().ShouldBe("mqtt-host:1883");
    }
}

[Collection("acceptance")]
public class named_connection_5126_end_to_end : IAsyncLifetime
{
    private LocalMqttBroker _brokerA = null!;
    private LocalMqttBroker _brokerB = null!;
    private int _portA;
    private int _portB;

    public async ValueTask InitializeAsync()
    {
        _portA = PortFinder.GetAvailablePort();
        _portB = PortFinder.GetAvailablePort();

        _brokerA = new LocalMqttBroker(_portA);
        _brokerB = new LocalMqttBroker(_portB);

        await _brokerA.StartAsync();
        await _brokerB.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _brokerA.DisposeAsync();
        await _brokerB.DisposeAsync();
    }

    [Fact]
    public async Task round_trips_messages_through_named_connections()
    {
        var topic = $"named-connection/{Guid.NewGuid():N}";
        var secondaryTopic = $"named-connection-2/{Guid.NewGuid():N}";
        var secondary = new BrokerName("secondary");

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // What an Aspire AppHost supplies through builder.AddMosquitto("mqtt") + WithReference(...)
                ["ConnectionStrings:mqtt"] = $"mqtt://127.0.0.1:{_portA}",
                ["ConnectionStrings:mqtt2"] = $"mqtt://127.0.0.1:{_portB}"
            }))
            .UseWolverine(opts =>
            {
                #region sample_mqtt_using_named_connection

                // Reads the broker URI (e.g. "mqtt://localhost:1883") from the "mqtt"
                // connection string in IConfiguration
                opts.UseMqttUsingNamedConnection("mqtt");

                #endregion

                #region sample_mqtt_named_broker_using_named_connection

                // A second MQTT broker, whose connection comes from the "mqtt2" connection string
                opts.AddNamedMqttBrokerUsingNamedConnection(new BrokerName("secondary"), "mqtt2");

                #endregion

                opts.Policies.DisableConventionalLocalRouting();

                opts.PublishMessage<NamedConnectionColor>().ToMqttTopic(topic);
                opts.ListenToMqttTopic(topic);

                opts.PublishMessage<NamedConnectionColor2>().ToMqttTopicOnNamedBroker(secondary, secondaryTopic);
                opts.ListenToMqttTopicOnNamedBroker(secondary, secondaryTopic);
            }).StartAsync(TestContext.Current.CancellationToken);

        var session = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<NamedConnectionColor>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new NamedConnectionColor("default")));

        session.Received.SingleMessage<NamedConnectionColor>().Color.ShouldBe("default");

        var session2 = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<NamedConnectionColor2>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new NamedConnectionColor2("named")));

        var received = session2.Received.SingleEnvelope<NamedConnectionColor2>();
        received.Message.ShouldBeOfType<NamedConnectionColor2>().Color.ShouldBe("named");
        received.Destination!.Scheme.ShouldBe("secondary");
    }
}

public record NamedConnectionColor(string Color);

public record NamedConnectionColor2(string Color);

public static class NamedConnectionColorHandler
{
    public static void Handle(NamedConnectionColor message)
    {
    }

    public static void Handle(NamedConnectionColor2 message)
    {
    }
}
