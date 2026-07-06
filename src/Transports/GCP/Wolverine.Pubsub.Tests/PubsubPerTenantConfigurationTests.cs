using Google.Api.Gax;
using JasperFx.Core;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Pubsub.Tests;

// Broker-per-tenant (GH-3306) unit coverage that needs NO emulator: it exercises the tenant registration / topology
// wiring, not live send/receive. Live tenant routing behavior is covered by PubsubPerTenantBrokerTests.
public class PubsubPerTenantConfigurationTests
{
    [Fact]
    public void add_tenant_registers_a_tenant_with_its_own_project()
    {
        var options = new WolverineOptions();
        options.UsePubsub("wolverine")
            .AddTenant("tenant2", "wolverine2");

        var transport = options.Transports.GetOrCreate<PubsubTransport>();
        transport.Tenants.Count().ShouldBe(1);

        var tenant = transport.Tenants["tenant2"];
        tenant.TenantId.ShouldBe("tenant2");
        tenant.ProjectId.ShouldBe("wolverine2");
    }

    [Fact]
    public void add_tenant_configure_action_can_override_emulator_detection()
    {
        var options = new WolverineOptions();
        options.UsePubsub("wolverine")
            .AddTenant("tenant2", "wolverine2", t => t.EmulatorDetection = EmulatorDetection.EmulatorOnly);

        var transport = options.Transports.GetOrCreate<PubsubTransport>();
        transport.Tenants["tenant2"].EmulatorDetection.ShouldBe(EmulatorDetection.EmulatorOnly);
    }

    [Fact]
    public void tenant_id_behavior_defaults_to_fallback_to_default()
    {
        var transport = new PubsubTransport { ProjectId = "wolverine" };
        transport.TenantedIdBehavior.ShouldBe(TenantedIdBehavior.FallbackToDefault);
    }

    [Fact]
    public void tenant_id_behavior_is_configurable()
    {
        var options = new WolverineOptions();
        options.UsePubsub("wolverine")
            .TenantIdBehavior(TenantedIdBehavior.TenantIdRequired);

        var transport = options.Transports.GetOrCreate<PubsubTransport>();
        transport.TenantedIdBehavior.ShouldBe(TenantedIdBehavior.TenantIdRequired);
    }

    [Fact]
    public void pubsub_endpoints_are_tenant_aware_by_default()
    {
        var transport = new PubsubTransport { ProjectId = "wolverine" };
        var topic = transport.Topics["orders"];
        topic.TenancyBehavior.ShouldBe(TenancyBehavior.TenantAware);
    }

    [Fact]
    public void tenant_topic_and_subscription_names_use_the_tenant_project_but_share_the_id()
    {
        var transport = new PubsubTransport { ProjectId = "wolverine" };
        var topic = transport.Topics["orders"];

        // Default project resolves to the endpoint's own names...
        topic.TopicNameFor("wolverine").ShouldBe(topic.Server.Topic.Name);
        topic.SubscriptionNameFor("wolverine").ShouldBe(topic.Server.Subscription.Name);

        // ...a tenant project yields a physically distinct resource sharing the same logical id.
        topic.TopicNameFor("wolverine2").ProjectId.ShouldBe("wolverine2");
        topic.TopicNameFor("wolverine2").TopicId.ShouldBe(topic.Server.Topic.Name.TopicId);
        topic.SubscriptionNameFor("wolverine2").ProjectId.ShouldBe("wolverine2");
        topic.SubscriptionNameFor("wolverine2").SubscriptionId.ShouldBe(topic.Server.Subscription.Name.SubscriptionId);
    }
}
