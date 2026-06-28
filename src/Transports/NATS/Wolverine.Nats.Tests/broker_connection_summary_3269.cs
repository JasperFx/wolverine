using JasperFx.Descriptors;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

// GH-3269: the NATS broker summary must be host:port with NO credentials, even though the connection string can embed
// userinfo (nats://user:pass@host) and the configuration carries Username/Password/Token.
public class broker_connection_summary_3269
{
    private const string Secret = "S3CR3T_NATS_PASSWORD";

    [Fact]
    public void describe_endpoint_strips_embedded_userinfo()
    {
        var transport = new NatsTransport();
        transport.Configuration.ConnectionString = $"nats://user:{Secret}@nats.host:4222";

        transport.DescribeEndpoint().ShouldBe("nats.host:4222");
    }

    [Fact]
    public void describe_endpoint_handles_a_server_list()
    {
        var transport = new NatsTransport();
        transport.Configuration.ConnectionString = "nats://one:4222, nats://two:4222";

        transport.DescribeEndpoint().ShouldBe("one:4222, two:4222");
    }

    [Fact]
    public void credentials_never_leak_into_the_description()
    {
        var transport = new NatsTransport();
        transport.Configuration.ConnectionString = $"nats://user:{Secret}@nats.host:4222";
        transport.Configuration.Username = "user";
        transport.Configuration.Password = Secret;
        transport.Configuration.Token = Secret;

        var description = new BrokerDescription(transport);

        description.Endpoint.ShouldBe("nats.host:4222");
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
