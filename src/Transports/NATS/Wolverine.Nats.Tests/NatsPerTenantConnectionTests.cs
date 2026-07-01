using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using Shouldly;
using Testcontainers.Nats;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Integration coverage for per-tenant NATS <em>connections</em> (as opposed to per-tenant subject
/// prefixing on one shared connection, which <see cref="MultiTenancyIntegrationTests"/> covers).
///
/// Answering the practical question "does per-tenant mean a different NATS setup?": yes — to prove a tenant
/// uses its <em>own</em> connection, that connection must point at a genuinely distinguishable server. This
/// test therefore spins up a second NATS broker (server B) via Testcontainers alongside the shared broker
/// (server A) and asserts:
/// <list type="bullet">
/// <item>a message for the tenant with a dedicated connection is published to <b>server B</b> and not server A,</item>
/// <item>a default (no-tenant) message is published to <b>server A</b> (the shared connection), and</item>
/// <item>a message tagged for the tenant is <b>consumed</b> back over the tenant's own connection (server B),
/// arriving stamped with its tenant id.</item>
/// </list>
///
/// The publish-side assertions use raw NATS subscribers rather than Wolverine receivers so the exact target
/// server is provable; the inbound test uses a single Wolverine host plus the tracking API. This test needs
/// two brokers, so it is intended as a local sanity check and is <b>not</b> required to run in CI.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsPerTenantConnectionTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private NatsContainer? _serverB;
    private string _serverAUrl = null!;
    private string _serverBUrl = null!;
    private bool _skip;

    public NatsPerTenantConnectionTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _serverAUrl = NatsTestHelpers.ResolveUrl();

        if (!await NatsTestHelpers.IsNatsAvailable(_serverAUrl))
        {
            _skip = true;
            return;
        }

        // Server B is a second, independent broker so "used the tenant's own connection" is provable: the
        // message can only appear on B if the dedicated connection carried it there.
        _serverB = new NatsBuilder().WithImage("nats:latest").Build();
        await _serverB.StartAsync();
        _serverBUrl = _serverB.GetConnectionString();

        _output.WriteLine($"Server A (shared): {_serverAUrl}");
        _output.WriteLine($"Server B (tenant): {_serverBUrl}");
    }

    public async Task DisposeAsync()
    {
        if (_serverB != null)
        {
            await _serverB.DisposeAsync();
        }
    }

    [Fact]
    public async Task tenant_message_is_published_over_the_tenants_own_connection()
    {
        if (_skip) return;

        var baseSubject = $"pertenant.{Guid.NewGuid():N}";
        var tenantSubject = $"tenantB.{baseSubject}"; // DefaultTenantSubjectMapper prefixes the tenant id

        await using var subOnB = await NatsTestHelpers.SubscribeRawAsync(_serverBUrl, tenantSubject);
        await using var subOnA = await NatsTestHelpers.SubscribeRawAsync(_serverAUrl, tenantSubject);

        using var host = await BuildSenderAsync(baseSubject);

        await host.MessageBus().SendAsync(new OrderPlaced("for-tenant-b"),
            new DeliveryOptions { TenantId = "tenantB" });

        // Landed on server B (the tenant's dedicated connection)...
        var received = await subOnB.ReadAsync(15.Seconds());
        received.ShouldNotBeNull();
        received!.Value.Subject.ShouldBe(tenantSubject);

        // ...and NOT on the shared server A.
        (await subOnA.ReadAsync(2.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task default_message_uses_the_shared_connection()
    {
        if (_skip) return;

        var baseSubject = $"pertenant.{Guid.NewGuid():N}";

        // No tenant prefix for the fallback/default path — it publishes to the base subject on server A.
        await using var subOnA = await NatsTestHelpers.SubscribeRawAsync(_serverAUrl, baseSubject);
        await using var subOnB = await NatsTestHelpers.SubscribeRawAsync(_serverBUrl, baseSubject);

        using var host = await BuildSenderAsync(baseSubject);

        await host.MessageBus().SendAsync(new OrderPlaced("no-tenant"));

        var received = await subOnA.ReadAsync(15.Seconds());
        received.ShouldNotBeNull();
        received!.Value.Subject.ShouldBe(baseSubject);

        (await subOnB.ReadAsync(2.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task tenant_message_is_consumed_over_the_tenants_own_connection()
    {
        if (_skip) return;

        var baseSubject = $"pertenant.{Guid.NewGuid():N}";

        // A single host both publishes and listens. The tenant listener runs on server B (the dedicated
        // connection), so a message tagged for tenantB round-trips back through it, stamped with the tenant id.
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantInbound";
                opts.UseNats(_serverAUrl)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenantB", cfg => cfg.ConnectionString = _serverBUrl);

                opts.PublishMessage<OrderPlaced>().ToNatsSubject(baseSubject).SendInline();
                opts.ListenToNatsSubject(baseSubject);
            })
            .StartAsync();

        // Single host both sends and receives via the broker, so explicitly wait for the round-trip receipt
        // rather than just the send settling.
        var session = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<OrderPlaced>(host)
            .ExecuteAndWaitAsync(c =>
                c.SendAsync(new OrderPlaced("for-tenant-b"), new DeliveryOptions { TenantId = "tenantB" }));

        var received = session.Received.SingleEnvelope<OrderPlaced>();
        received.TenantId.ShouldBe("tenantB");
        received.Message.ShouldBeOfType<OrderPlaced>().OrderId.ShouldBe("for-tenant-b");
    }

    [Fact]
    public async Task streams_are_auto_provisioned_over_a_tenants_own_connection()
    {
        if (_skip) return;

        var streamName = $"TENANTPROV_{Guid.NewGuid():N}";
        var subject = $"tenantprov.{Guid.NewGuid():N}";

        // A tenant with its own connection to server B, plus a configured stream + AutoProvision. Stream
        // provisioning runs at host start over the tenant's dedicated connection — which is never explicitly
        // ConnectAsync'd; the NATS client connects lazily on first use. If that path were broken, StartAsync
        // would throw right here.
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantProvisioning";
                opts.UseNats(_serverAUrl)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AutoProvision()
                    .DefineStream(streamName, s => s.WithSubjects($"{subject}.>"))
                    .AddTenant("tenantB", cfg => cfg.ConnectionString = _serverBUrl);
            })
            .StartAsync();

        // Prove the stream was actually created on server B (the tenant's own server), not just server A.
        // GetStreamAsync throws if the stream is absent, so a broken provisioning path fails the test.
        await using var connToB = new NatsConnection(new NatsOpts { Url = _serverBUrl });
        await connToB.ConnectAsync();
        var streamOnB = await connToB.CreateJetStreamContext().GetStreamAsync(streamName);
        streamOnB.ShouldNotBeNull();
    }

    private Task<IHost> BuildSenderAsync(string baseSubject)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantSender";
                opts.UseNats(_serverAUrl)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    // The tenant gets its own connection to server B; the action is seeded from the parent
                    // settings so we only override the URL that differs.
                    .AddTenant("tenantB", cfg => cfg.ConnectionString = _serverBUrl);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(baseSubject).SendInline();
            })
            .StartAsync();
    }
}
