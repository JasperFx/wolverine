using Amazon;
using Amazon.Runtime;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.Configuration;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AmazonSns.Tests;

// Broker-per-tenant (GH-3305) unit coverage that needs NO SNS broker/LocalStack: it exercises the tenant
// registration / compile / sender-resolution wiring, not live publishing. Live tenant routing behavior is
// covered by AmazonSnsPerTenantConnectionTests.
public class AmazonSnsPerTenantConfigurationTests
{
    [Fact]
    public void add_tenant_registers_a_tenant_with_its_own_region()
    {
        var options = new WolverineOptions();
        var sns = options.UseAmazonSnsTransport(c => c.RegionEndpoint = RegionEndpoint.USEast1);
        sns.AddTenant("tenantB", c => c.RegionEndpoint = RegionEndpoint.USWest2);

        var transport = options.AmazonSnsTransport();
        transport.Tenants.Count().ShouldBe(1);

        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, null!);

        tenant.Transport.SnsConfig.RegionEndpoint.ShouldBe(RegionEndpoint.USWest2);
    }

    [Fact]
    public void add_tenant_with_credentials_sets_a_dedicated_credential_source()
    {
        var options = new WolverineOptions();
        var sns = options.UseAmazonSnsTransport(c => c.RegionEndpoint = RegionEndpoint.USEast1);

        var credentials = new BasicAWSCredentials("tenant-key", "tenant-secret");
        sns.AddTenant("tenantB", credentials, c => c.RegionEndpoint = RegionEndpoint.EUWest1);

        var transport = options.AmazonSnsTransport();
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, null!);

        var credentialSource = tenant.Transport.CredentialSource;
        credentialSource.ShouldNotBeNull();
        credentialSource(null!).ShouldBeSameAs(credentials);
        tenant.Transport.SnsConfig.RegionEndpoint.ShouldBe(RegionEndpoint.EUWest1);
    }

    [Fact]
    public void tenant_inherits_parent_credentials_when_not_overridden()
    {
        var options = new WolverineOptions();
        var parentCredentials = new BasicAWSCredentials("parent-key", "parent-secret");
        var sns = options.UseAmazonSnsTransport(c => c.RegionEndpoint = RegionEndpoint.USEast1);
        sns.Credentials(parentCredentials);
        sns.AddTenant("tenantB", c => c.RegionEndpoint = RegionEndpoint.USWest2);

        var transport = options.AmazonSnsTransport();
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, null!);

        // Inherited the parent credential source (dedicated-region tenant, shared account)...
        tenant.Transport.CredentialSource.ShouldBeSameAs(transport.CredentialSource);
        // ...but kept its own region.
        tenant.Transport.SnsConfig.RegionEndpoint.ShouldBe(RegionEndpoint.USWest2);
    }

    [Fact]
    public void tenant_inherits_parent_region_and_provisioning_when_it_sets_nothing()
    {
        var options = new WolverineOptions();
        var sns = options.UseAmazonSnsTransport(c => c.RegionEndpoint = RegionEndpoint.USEast1);
        sns.AutoProvision();
        // Register a tenant that only supplies credentials, no region/endpoint of its own.
        sns.AddTenant("tenantB", new BasicAWSCredentials("k", "s"));

        var transport = options.AmazonSnsTransport();
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, null!);

        tenant.Transport.SnsConfig.RegionEndpoint.ShouldBe(RegionEndpoint.USEast1);
        tenant.Transport.AutoProvision.ShouldBeTrue();
    }

    [Fact]
    public void paired_sqs_client_tracks_the_tenant_sns_account()
    {
        var options = new WolverineOptions();
        var sns = options.UseAmazonSnsTransport(c => c.ServiceURL = "http://localhost:4566");
        sns.AddTenant("tenantB", c => c.AuthenticationRegion = "us-west-2");

        var transport = options.AmazonSnsTransport();
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, null!);

        // The paired SQS client used for subscription provisioning must sign for the same account/region as the
        // tenant's SNS topic so cross-partition provisioning targets the tenant's own store.
        tenant.Transport.SQS.Config.ServiceURL.ShouldBe(tenant.Transport.SnsConfig.ServiceURL);
        tenant.Transport.SQS.Config.ServiceURL.ShouldStartWith("http://localhost:4566");
        tenant.Transport.SQS.Config.AuthenticationRegion.ShouldBe("us-west-2");
    }

    [Fact]
    public void sns_topics_are_tenant_aware_by_default()
    {
        var transport = new AmazonSnsTransport();
        var topic = transport.Topics["orders"];
        topic.TenancyBehavior.ShouldBe(TenancyBehavior.TenantAware);
    }

    [Fact]
    public void build_tenant_sibling_copies_configuration_but_uses_the_tenant_transport()
    {
        var transport = new AmazonSnsTransport { SQS = new AmazonSqs.Internal.AmazonSqsTransport() };
        var tenant = transport.Tenants["tenantB"];

        var topic = transport.Topics["orders"];
        topic.TopicSubscriptions.Add(new AmazonSnsSubscription("some-queue", AmazonSnsSubscriptionType.Sqs,
            new AmazonSnsSubscriptionAttributes { RawMessageDelivery = true }));

        var sibling = topic.BuildTenantSibling(tenant);

        sibling.ShouldNotBeSameAs(topic);
        sibling.TopicName.ShouldBe("orders");
        // Subscriptions are deep-copied, not shared (SubscriptionArn is stamped per account).
        sibling.TopicSubscriptions.ShouldNotBeSameAs(topic.TopicSubscriptions);
        sibling.TopicSubscriptions.Single().Endpoint.ShouldBe("some-queue");
        sibling.TopicSubscriptions.Single().Attributes.RawMessageDelivery.ShouldBeTrue();
        // Resolved from the tenant transport's own (isolated) topic cache.
        tenant.Transport.Topics["orders"].ShouldBeSameAs(sibling);
    }

    [Fact]
    public void tenant_id_behavior_defaults_to_fallback_and_is_overridable()
    {
        var options = new WolverineOptions();
        var sns = options.UseAmazonSnsTransport();

        var transport = options.AmazonSnsTransport();
        transport.TenantedIdBehavior.ShouldBe(TenantedIdBehavior.FallbackToDefault);

        sns.TenantIdBehavior(TenantedIdBehavior.TenantIdRequired);
        transport.TenantedIdBehavior.ShouldBe(TenantedIdBehavior.TenantIdRequired);
    }
}
