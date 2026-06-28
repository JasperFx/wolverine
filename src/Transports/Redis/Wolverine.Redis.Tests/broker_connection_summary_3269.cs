using JasperFx.Descriptors;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.Redis.Internal;
using Xunit;

namespace Wolverine.Redis.Tests;

// GH-3269: the Redis summary is host:port, never the password from the connection string.
public class broker_connection_summary_3269
{
    private const string Secret = "S3CR3T_REDIS_PWD";

    [Fact]
    public void describe_endpoint_reports_host_and_port()
    {
        var transport = new RedisTransport($"redis.host:6379,password={Secret}");
        transport.DescribeEndpoint().ShouldBe("redis.host:6379");
    }

    [Fact]
    public void password_never_leaks_into_the_description()
    {
        var transport = new RedisTransport($"redis.host:6379,password={Secret}");

        var description = new BrokerDescription(transport);

        description.Endpoint.ShouldBe("redis.host:6379");
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
