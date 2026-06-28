using JasperFx.Descriptors;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

// GH-3269: the RabbitMQ broker summary is host:port (vhost: …) built from the connection factory, never the password.
public class broker_connection_summary_3269
{
    private const string Secret = "S3CR3T_RABBIT_PWD";

    [Fact]
    public void describe_endpoint_reports_host_port_and_vhost()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(f =>
        {
            f.HostName = "rabbit.host";
            f.Port = 5672;
            f.VirtualHost = "/prod";
            f.UserName = "guest";
            f.Password = Secret;
        });

        transport.DescribeEndpoint().ShouldBe("rabbit.host:5672 (vhost: /prod)");
    }

    [Fact]
    public void password_never_leaks_into_the_description()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(f =>
        {
            f.HostName = "rabbit.host";
            f.Port = 5672;
            f.Password = Secret;
        });

        var description = new BrokerDescription(transport);

        description.Endpoint.ShouldBe("rabbit.host:5672 (vhost: /)");
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
