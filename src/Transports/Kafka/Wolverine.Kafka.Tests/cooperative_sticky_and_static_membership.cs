using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

// GH-3139: static membership config surface. No broker required — these assert the resolved
// consumer options and the instance-id resolution precedence. (Cooperative-sticky assignment is
// gone: under the KIP-848-only Dekaf client, rebalances are always broker-driven and incremental.)
public class cooperative_sticky_and_static_membership
{
    private static KafkaTopic apply(Action<KafkaListenerConfiguration> configure)
    {
        var transport = new KafkaTransport();
        var topic = transport.Topics["orders"];
        var config = new KafkaListenerConfiguration(topic);
        configure(config);
        ((IDelayedEndpointConfiguration)config).Apply();
        return topic;
    }

    // ---- transport scope ----

    [Fact]
    public void transport_static_membership_with_explicit_id()
    {
        var transport = new KafkaTransport();
        new KafkaTransportExpression(transport, new WolverineOptions()).UseStaticMembership("node-7");

        transport.ConsumerConfig.GroupInstanceId.ShouldBe("node-7");
        transport.StaticMembershipRequested.ShouldBeTrue();
    }

    [Fact]
    public void transport_static_membership_defaults_to_env()
    {
        var prior = Environment.GetEnvironmentVariable("POD_NAME");
        Environment.SetEnvironmentVariable("POD_NAME", "pod-abc");
        try
        {
            var transport = new KafkaTransport();
            new KafkaTransportExpression(transport, new WolverineOptions()).UseStaticMembership();

            transport.ConsumerConfig.GroupInstanceId.ShouldBe("pod-abc");
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAME", prior);
        }
    }

    // ---- listener scope ----

    [Fact]
    public void listener_static_membership_with_explicit_id()
    {
        var topic = apply(c => c.UseStaticMembership("node-9"));
        topic.ConsumerConfig!.GroupInstanceId.ShouldBe("node-9");
        topic.StaticMembershipRequested.ShouldBeTrue();
    }

    [Fact]
    public void listener_static_membership_with_source_func()
    {
        var topic = apply(c => c.UseStaticMembership(() => "from-func"));
        topic.ConsumerConfig!.GroupInstanceId.ShouldBe("from-func");
    }

    [Fact]
    public void listener_combines_with_configure_consumer_when_ordered_after()
    {
        var topic = apply(c => c
            .ConfigureConsumer(x => x.GroupId = "grp")
            .UseStaticMembership("node-1"));

        topic.ConsumerConfig!.GroupId.ShouldBe("grp");
        topic.ConsumerConfig.GroupInstanceId.ShouldBe("node-1");
    }

    // ---- resolution precedence ----

    [Fact]
    public void resolve_prefers_explicit_source_over_env()
    {
        var prior = Environment.GetEnvironmentVariable("POD_NAME");
        Environment.SetEnvironmentVariable("POD_NAME", "pod-x");
        try
        {
            KafkaStaticMembership.Resolve(() => "explicit").ShouldBe("explicit");
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAME", prior);
        }
    }

    [Fact]
    public void resolve_blank_source_falls_through_to_env()
    {
        var prior = Environment.GetEnvironmentVariable("POD_NAME");
        Environment.SetEnvironmentVariable("POD_NAME", "pod-y");
        try
        {
            KafkaStaticMembership.Resolve(() => "   ").ShouldBe("pod-y");
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAME", prior);
        }
    }

    [Fact]
    public void resolve_falls_back_to_machine_name_when_no_env()
    {
        var priorPod = Environment.GetEnvironmentVariable("POD_NAME");
        var priorHost = Environment.GetEnvironmentVariable("HOSTNAME");
        Environment.SetEnvironmentVariable("POD_NAME", null);
        Environment.SetEnvironmentVariable("HOSTNAME", null);
        try
        {
            KafkaStaticMembership.Resolve(null).ShouldBe(Environment.MachineName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAME", priorPod);
            Environment.SetEnvironmentVariable("HOSTNAME", priorHost);
        }
    }
}
