using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Kafka.Tests;

// Broker-per-tenant (GH-3303) unit coverage that needs NO Kafka broker: it exercises the tenant compile /
// sender-resolution wiring, not live produce/consume. Live cluster-routing behavior is covered by
// KafkaPerTenantConnectionTests.
public class KafkaPerTenantConfigurationTests
{
    [Fact]
    public void add_tenant_registers_a_tenant_with_its_own_bootstrap_servers()
    {
        var options = new WolverineOptions();
        var kafka = options.UseKafka("shared:9092");
        kafka.ConfigureConsumers(c => c.GroupId = "shared-group");
        kafka.AddTenant("tenantB", "tenant-b:9092");

        var transport = kafka.Transport;
        transport.Tenants.Count().ShouldBe(1);

        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, options);

        tenant.Transport.ProducerConfig.BootstrapServers.ShouldBe("tenant-b:9092");
        tenant.Transport.ConsumerConfig.BootstrapServers.ShouldBe("tenant-b:9092");
        tenant.Transport.AdminClientConfig.BootstrapServers.ShouldBe("tenant-b:9092");
    }

    [Fact]
    public void tenant_consumer_group_id_is_NOT_suffixed_per_tenant()
    {
        // Each tenant is a separate cluster, so offsets are isolated and the group id must stay identical to
        // the shared cluster's — suffixing it per tenant would be a bug (unlike NATS subject prefixing).
        var options = new WolverineOptions();
        var kafka = options.UseKafka("shared:9092");
        kafka.ConfigureConsumers(c => c.GroupId = "shared-group");
        kafka.AddTenant("tenantB", "tenant-b:9092");

        var transport = kafka.Transport;
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, options);

        tenant.Transport.ConsumerConfig.GroupId.ShouldBe("shared-group");
        tenant.Transport.ConsumerConfig.GroupId.ShouldBe(transport.ConsumerConfig.GroupId);
    }

    [Fact]
    public void tenant_inherits_parent_client_configuration()
    {
        // SASL / SSL / idempotence / DLQ topic name and other parent settings must carry over to the tenant
        // cluster; only the bootstrap servers differ.
        var options = new WolverineOptions();
        var kafka = options.UseKafka("shared:9092");
        kafka.ConfigureProducers(p => p.EnableIdempotence = true);
        kafka.ConfigureClient(c => c.SaslUsername = "shared-user");
        kafka.DeadLetterQueueTopicName("shared-dlq");
        kafka.AddTenant("tenantB", "tenant-b:9092");

        var transport = kafka.Transport;
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, options);

        tenant.Transport.ProducerConfig.EnableIdempotence.ShouldBe(true);
        tenant.Transport.ProducerConfig.SaslUsername.ShouldBe("shared-user");
        tenant.Transport.DeadLetterQueueTopicName.ShouldBe("shared-dlq");
    }

    [Fact]
    public void add_tenant_with_configure_action_is_seeded_from_parent_and_can_override()
    {
        var options = new WolverineOptions();
        var kafka = options.UseKafka("shared:9092");
        kafka.ConfigureClient(c => c.SaslUsername = "shared-user");
        kafka.AddTenant("tenantB", t => t.ConfigureClient(c =>
        {
            c.BootstrapServers = "tenant-b:9092";
            c.SaslUsername = "tenant-b-user";
        }));

        var transport = kafka.Transport;
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport, options);

        // seeded from parent...
        tenant.Transport.ProducerConfig.BootstrapServers.ShouldBe("tenant-b:9092");
        // ...and overridden by the tenant action
        tenant.Transport.ProducerConfig.SaslUsername.ShouldBe("tenant-b-user");
        tenant.Transport.ConsumerConfig.SaslUsername.ShouldBe("tenant-b-user");
    }

    [Fact]
    public void kafka_topics_are_tenant_aware_by_default()
    {
        var transport = new KafkaTransport();
        var topic = transport.Topics["orders"];
        topic.TenancyBehavior.ShouldBe(TenancyBehavior.TenantAware);
    }

    [Fact]
    public async Task tenant_aware_endpoint_resolves_a_TenantedSender()
    {
        // No broker needed: resolving the sender builds (lazy) Confluent producers but never connects.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092")
                    .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenantB", "localhost:9192");

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColor>().ToKafkaTopic("tenant-colors");
            })
            .StartAsync();

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<KafkaTransport>();
        var topic = transport.Topics["tenant-colors"];

        var agent = (SendingAgent)runtime.Endpoints.GetOrBuildSendingAgent(topic.Uri);
        agent.Sender.ShouldBeOfType<TenantedSender>();
    }
}

public record TenantColor(string Color);
