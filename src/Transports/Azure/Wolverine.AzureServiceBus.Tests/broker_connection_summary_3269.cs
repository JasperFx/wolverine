using JasperFx.Descriptors;
using Shouldly;
using Wolverine.AzureServiceBus;
using Wolverine.Configuration.Capabilities;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

// GH-3269: the Azure Service Bus summary is the namespace FQDN, never the SharedAccessKey.
public class broker_connection_summary_3269
{
    private const string Secret = "aSharedAccessKeySecretValue123=";

    [Fact]
    public void describe_endpoint_uses_the_fully_qualified_namespace()
    {
        var transport = new AzureServiceBusTransport { FullyQualifiedNamespace = "myns.servicebus.windows.net" };
        transport.DescribeEndpoint().ShouldBe("myns.servicebus.windows.net");
    }

    [Fact]
    public void describe_endpoint_extracts_namespace_from_connection_string()
    {
        var transport = new AzureServiceBusTransport
        {
            ConnectionString =
                $"Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey={Secret}"
        };

        transport.DescribeEndpoint().ShouldBe("myns.servicebus.windows.net");
    }

    [Fact]
    public void shared_access_key_never_leaks_into_the_description()
    {
        var transport = new AzureServiceBusTransport
        {
            ConnectionString =
                $"Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey={Secret}"
        };

        var description = new BrokerDescription(transport);

        description.Endpoint.ShouldBe("myns.servicebus.windows.net");
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
