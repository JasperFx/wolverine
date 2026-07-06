using Amazon;
using Amazon.Runtime;
using JasperFx.Core;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

// Broker-per-tenant (GH-3304) unit coverage that needs NO SQS broker/LocalStack: it exercises the tenant
// registration / compile / sender-resolution wiring, not live send/receive. Live tenant routing behavior is
// covered by AmazonSqsPerTenantConnectionTests.
public class AmazonSqsPerTenantConfigurationTests
{
    [Fact]
    public void add_tenant_registers_a_tenant_with_its_own_region()
    {
        var options = new WolverineOptions();
        var sqs = options.UseAmazonSqsTransport(c => c.RegionEndpoint = RegionEndpoint.USEast1);
        sqs.AddTenant("tenantB", c => c.RegionEndpoint = RegionEndpoint.USWest2);

        var transport = options.Transports.GetOrCreate<AmazonSqsTransport>();
        transport.Tenants.Count().ShouldBe(1);

        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, null!);

        tenant.Transport.Config.RegionEndpoint.ShouldBe(RegionEndpoint.USWest2);
    }

    [Fact]
    public void add_tenant_with_credentials_sets_a_dedicated_credential_source()
    {
        var options = new WolverineOptions();
        var sqs = options.UseAmazonSqsTransport(c => c.RegionEndpoint = RegionEndpoint.USEast1);

        var credentials = new BasicAWSCredentials("tenant-key", "tenant-secret");
        sqs.AddTenant("tenantB", credentials, c => c.RegionEndpoint = RegionEndpoint.EUWest1);

        var transport = options.Transports.GetOrCreate<AmazonSqsTransport>();
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, null!);

        var credentialSource = tenant.Transport.CredentialSource;
        credentialSource.ShouldNotBeNull();
        credentialSource(null!).ShouldBeSameAs(credentials);
        tenant.Transport.Config.RegionEndpoint.ShouldBe(RegionEndpoint.EUWest1);
    }

    [Fact]
    public void tenant_inherits_parent_credentials_when_not_overridden()
    {
        var options = new WolverineOptions();
        var parentCredentials = new BasicAWSCredentials("parent-key", "parent-secret");
        var sqs = options.UseAmazonSqsTransport(c => c.RegionEndpoint = RegionEndpoint.USEast1);
        sqs.Credentials(parentCredentials);
        sqs.AddTenant("tenantB", c => c.RegionEndpoint = RegionEndpoint.USWest2);

        var transport = options.Transports.GetOrCreate<AmazonSqsTransport>();
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, null!);

        // Inherited the parent credential source (dedicated-region tenant, shared account)...
        tenant.Transport.CredentialSource.ShouldBeSameAs(transport.CredentialSource);
        // ...but kept its own region.
        tenant.Transport.Config.RegionEndpoint.ShouldBe(RegionEndpoint.USWest2);
    }

    [Fact]
    public void tenant_inherits_parent_region_and_dlq_behavior_when_it_sets_neither_service_url_nor_region()
    {
        var options = new WolverineOptions();
        var sqs = options.UseAmazonSqsTransport(c => c.RegionEndpoint = RegionEndpoint.USEast1);
        sqs.DefaultDeadLetterQueueName("shared-dlq");
        // Register a tenant that only supplies credentials, no region/endpoint of its own.
        sqs.AddTenant("tenantB", new BasicAWSCredentials("k", "s"));

        var transport = options.Transports.GetOrCreate<AmazonSqsTransport>();
        transport.AutoProvision = true;
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, null!);

        tenant.Transport.Config.RegionEndpoint.ShouldBe(RegionEndpoint.USEast1);
        tenant.Transport.AutoProvision.ShouldBeTrue();
        tenant.Transport.DefaultDeadLetterQueueName.ShouldBe("shared-dlq");
    }

    [Fact]
    public void sqs_queues_are_tenant_aware_by_default()
    {
        var transport = new AmazonSqsTransport();
        var queue = transport.Queues["orders"];
        queue.TenancyBehavior.ShouldBe(TenancyBehavior.TenantAware);
    }

    [Fact]
    public void build_tenant_sibling_copies_configuration_but_uses_the_tenant_transport()
    {
        var transport = new AmazonSqsTransport();
        var tenant = transport.Tenants["tenantB"];

        var queue = transport.Queues["orders"];
        queue.VisibilityTimeout = 42;
        queue.MaxNumberOfMessages = 7;
        queue.EnableFairQueueMessageGroups = true;

        var sibling = queue.BuildTenantSibling(tenant);

        sibling.ShouldNotBeSameAs(queue);
        sibling.QueueName.ShouldBe("orders");
        sibling.VisibilityTimeout.ShouldBe(42);
        sibling.MaxNumberOfMessages.ShouldBe(7);
        sibling.EnableFairQueueMessageGroups.ShouldBeTrue();
        // Resolved from the tenant transport's own (isolated) queue cache.
        tenant.Transport.Queues["orders"].ShouldBeSameAs(sibling);
    }
}

public record TenantColorMessage(string Color);

public static class TenantColorMessageHandler
{
    public static void Handle(TenantColorMessage message)
    {
        // no-op; presence lets Wolverine discover a handler so receive tests can track processing
    }
}
