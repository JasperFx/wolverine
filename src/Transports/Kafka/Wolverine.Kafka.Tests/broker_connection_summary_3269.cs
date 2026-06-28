using JasperFx.Descriptors;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.Kafka.Internals;
using Xunit;

namespace Wolverine.Kafka.Tests;

// GH-3269: BrokerDescription must carry a sanitized, credential-free connection summary per transport. For Kafka the
// summary is the bootstrap servers, and the SASL credentials on the Confluent configs must NEVER leak into the
// diagnostic tree.
public class broker_connection_summary_3269
{
    private const string Secret = "S3CR3T_SASL_PASSWORD";

    [Fact]
    public void describe_endpoint_reports_bootstrap_servers()
    {
        var transport = new KafkaTransport();
        transport.ProducerConfig.BootstrapServers = "broker1:9092,broker2:9092";

        transport.DescribeEndpoint().ShouldBe("broker1:9092,broker2:9092");
    }

    [Fact]
    public void broker_description_endpoint_is_populated()
    {
        var transport = new KafkaTransport();
        transport.ConsumerConfig.BootstrapServers = "kafka.internal:9092";

        new BrokerDescription(transport).Endpoint.ShouldBe("kafka.internal:9092");
    }

    [Fact]
    public void sasl_credentials_never_leak_into_the_description()
    {
        var transport = new KafkaTransport();
        transport.ProducerConfig.BootstrapServers = "broker:9092";
        transport.ProducerConfig.SaslUsername = "admin";
        transport.ProducerConfig.SaslPassword = Secret;
        transport.ConsumerConfig.SaslPassword = Secret;
        transport.AdminClientConfig.SaslPassword = Secret;

        var description = new BrokerDescription(transport);

        // The sanitized target is present, the secret is not — anywhere in the reflected diagnostic tree.
        description.Endpoint.ShouldBe("broker:9092");
        BrokerSecretScanner.AssertNoSecret(description, Secret);
    }
}

// Shared, project-local helper: recursively scan an OptionsDescription (Properties, Children, Sets) plus the typed
// BrokerDescription fields for a forbidden secret substring.
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
