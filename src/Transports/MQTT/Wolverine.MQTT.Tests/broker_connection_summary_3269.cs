using JasperFx.Descriptors;
using MQTTnet;
using Shouldly;
using System.Net;
using Wolverine.Configuration.Capabilities;
using Wolverine.MQTT.Internals;
using Xunit;

namespace Wolverine.MQTT.Tests;

// GH-3269: the MQTT summary is the broker host:port, never the username/password in the client options.
public class broker_connection_summary_3269
{
    private const string Secret = "S3CR3T_MQTT_PWD";

    [Fact]
    public void describe_endpoint_reports_tcp_host_and_port()
    {
        var transport = new MqttTransport();
        transport.Options.ClientOptions.ChannelOptions = new MqttClientTcpOptions
        {
            RemoteEndpoint = new DnsEndPoint("mqtt.host", 8883)
        };

        transport.DescribeEndpoint().ShouldBe("mqtt.host:8883");
    }

    [Fact]
    public void credentials_never_leak_into_the_description()
    {
        var transport = new MqttTransport();
        transport.Options.ClientOptions.ChannelOptions = new MqttClientTcpOptions
        {
            RemoteEndpoint = new DnsEndPoint("mqtt.host", 1883)
        };
        transport.Options.ClientOptions.Credentials =
            new MqttClientCredentials("user", System.Text.Encoding.UTF8.GetBytes(Secret));

        var description = new BrokerDescription(transport);

        description.Endpoint.ShouldBe("mqtt.host:1883");
        BrokerSecretScanner.AssertNoSecret(description, Secret);
    }
}

internal static class BrokerSecretScanner
{
    public static void AssertNoSecret(BrokerDescription description, string secret)
    {
        (description.Endpoint ?? "").ShouldNotContain(secret);
        foreach (var text in AllText(description))
        {
            text.ShouldNotContain(secret);
        }
    }

    private static IEnumerable<string> AllText(OptionsDescription description)
    {
        if (description.Subject != null) yield return description.Subject;
        foreach (var p in description.Properties)
        {
            if (p.Name != null) yield return p.Name;
            if (p.Value != null) yield return p.Value;
        }

        foreach (var child in description.Children.Values)
        foreach (var text in AllText(child))
            yield return text;

        foreach (var set in description.Sets.Values)
        foreach (var row in set.Rows)
        foreach (var text in AllText(row))
            yield return text;
    }
}
