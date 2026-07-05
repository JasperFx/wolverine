using Confluent.Kafka;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Testcontainers.Kafka;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// Integration coverage for broker-per-tenant Kafka (GH-3303): each tenant is served by its own dedicated Kafka
/// cluster while sharing the topic topology. To prove a tenant message uses its <em>own</em> cluster, that
/// cluster must be genuinely distinguishable, so this test runs a second Kafka broker (cluster B) via
/// Testcontainers alongside the docker-compose broker (cluster A, localhost:9092) and asserts:
/// <list type="bullet">
/// <item>a <c>DeliveryOptions{TenantId="tenantB"}</c> message lands on cluster B and NOT cluster A,</item>
/// <item>a default (no-tenant) message lands on cluster A (the shared cluster),</item>
/// <item>a message tagged for the tenant is consumed back over the tenant cluster, stamped with its tenant id.</item>
/// </list>
///
/// The publish-side assertions use raw Confluent consumers per bootstrap-servers so the exact target cluster is
/// provable. This test needs two brokers, so it is a local sanity check and is <b>not</b> required to run in CI.
/// </summary>
[Collection("Kafka Integration")]
[Trait("Category", "Integration")]
public class KafkaPerTenantConnectionTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private KafkaContainer? _clusterB;
    private string _clusterAServers = KafkaContainerFixture.ConnectionString; // docker-compose kafka (localhost:9092)
    private string _clusterBServers = null!;
    private bool _skip;

    public KafkaPerTenantConnectionTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        if (!IsKafkaAvailable(_clusterAServers))
        {
            _skip = true;
            return;
        }

        try
        {
            _clusterB = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
            await _clusterB.StartAsync();
            _clusterBServers = StripScheme(_clusterB.GetBootstrapAddress());

            _output.WriteLine($"Cluster A (shared): {_clusterAServers}");
            _output.WriteLine($"Cluster B (tenant): {_clusterBServers}");
        }
        catch (Exception e)
        {
            // Docker not available for the second cluster — skip rather than fail CI.
            _output.WriteLine("Skipping: could not start tenant Kafka cluster: " + e.Message);
            _skip = true;
        }
    }

    public async Task DisposeAsync()
    {
        if (_clusterB != null)
        {
            await _clusterB.DisposeAsync();
        }
    }

    [Fact]
    public async Task tenant_message_is_published_to_the_tenant_cluster_and_not_the_default()
    {
        if (_skip) return;

        var topic = $"pertenant.{Guid.NewGuid():N}";

        using var host = await BuildSenderAsync(topic);

        await host.MessageBus().SendAsync(new TenantColor("for-tenant-b"),
            new DeliveryOptions { TenantId = "tenantB" });

        // Landed on cluster B (the tenant's dedicated cluster)...
        (await ConsumeOne(_clusterBServers, topic, 20.Seconds())).ShouldNotBeNull();
        // ...and NOT on the shared cluster A.
        (await ConsumeOne(_clusterAServers, topic, 3.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task default_message_is_published_to_the_shared_cluster()
    {
        if (_skip) return;

        var topic = $"pertenant.{Guid.NewGuid():N}";

        using var host = await BuildSenderAsync(topic);

        await host.MessageBus().SendAsync(new TenantColor("no-tenant"));

        // Falls back to the default/shared cluster A (TenantedIdBehavior.FallbackToDefault)...
        (await ConsumeOne(_clusterAServers, topic, 20.Seconds())).ShouldNotBeNull();
        // ...and NOT the tenant cluster B.
        (await ConsumeOne(_clusterBServers, topic, 3.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task tenant_message_is_consumed_and_stamped_with_the_tenant_id()
    {
        if (_skip) return;

        var topic = $"pertenant.{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantInbound";
                opts.UseKafka(_clusterAServers)
                    .AutoProvision()
                    // Read from earliest so a just-produced record is caught even if the consumer group's
                    // partition assignment lands after the send (a Latest consumer would race the producer).
                    .BeginAtEarliest()
                    .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenantB", _clusterBServers);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColor>().ToKafkaTopic(topic);
                opts.ListenToKafkaTopic(topic);

                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(new OutputLoggerProvider(_output));
            })
            .StartAsync();

        // Single host both publishes (to the tenant cluster) and listens (on the tenant cluster), so the
        // message round-trips back stamped with the tenant id.
        var session = await host
            .TrackActivity()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<TenantColor>(host)
            .ExecuteAndWaitAsync(c =>
                c.SendAsync(new TenantColor("for-tenant-b"), new DeliveryOptions { TenantId = "tenantB" }));

        var received = session.Received.SingleEnvelope<TenantColor>();
        received.TenantId.ShouldBe("tenantB");
        received.Message.ShouldBeOfType<TenantColor>().Color.ShouldBe("for-tenant-b");
    }

    private Task<IHost> BuildSenderAsync(string topic)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantSender";
                opts.UseKafka(_clusterAServers)
                    .AutoProvision()
                    .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenantB", _clusterBServers);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColor>().ToKafkaTopic(topic).SendInline();
            })
            .StartAsync();
    }

    // Read a single record from the given cluster/topic within the timeout, or null if none arrives. Uses a
    // fresh consumer group + Earliest so a just-produced record is caught regardless of subscribe timing.
    private static async Task<ConsumeResult<string, byte[]>?> ConsumeOne(string bootstrapServers, string topic, TimeSpan timeout)
    {
        return await Task.Run(() =>
        {
            using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = $"verify-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                AllowAutoCreateTopics = true
            }).Build();

            consumer.Subscribe(topic);
            try
            {
                return consumer.Consume(timeout);
            }
            finally
            {
                consumer.Close();
            }
        });
    }

    private static bool IsKafkaAvailable(string bootstrapServers)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
            admin.GetMetadata(TimeSpan.FromSeconds(5));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Testcontainers' GetBootstrapAddress() returns a scheme-prefixed address (e.g. "PLAINTEXT://127.0.0.1:port");
    // Confluent's BootstrapServers wants a bare host:port list.
    private static string StripScheme(string address)
    {
        var idx = address.IndexOf("://", StringComparison.Ordinal);
        var hostPort = idx >= 0 ? address[(idx + 3)..] : address;
        return hostPort.TrimEnd('/');
    }
}

public static class TenantColorHandler
{
    public static void Handle(TenantColor message)
    {
        // no-op; presence lets Wolverine discover a handler so the receive test can track processing
    }
}
