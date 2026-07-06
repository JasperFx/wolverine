using System;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Transports.Sending;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// Integration coverage for broker-per-tenant Redis <em>connections</em>: each tenant talks to its own
/// dedicated Redis server. To prove a tenant uses its <em>own</em> connection, that connection must point at
/// a genuinely distinguishable server, so this test spins up a second Redis server (server B) via
/// Testcontainers alongside the shared server (server A) and asserts:
/// <list type="bullet">
/// <item>a message for the tenant with a dedicated connection is published to <b>server B</b> and not server A,</item>
/// <item>a default (no-tenant) message is published to <b>server A</b> (the shared connection), and</item>
/// <item>a message tagged for the tenant is <b>consumed</b> back over the tenant's own connection (server B),
/// arriving stamped with its tenant id.</item>
/// </list>
///
/// The publish-side assertions use a raw <c>XREAD</c> against each server so the exact target is provable;
/// the inbound test uses a single Wolverine host plus the tracking API.
/// </summary>
public class RedisPerTenantConnectionTests : IClassFixture<SecondRedisServerFixture>
{
    private readonly string _serverAConn = RedisContainerFixture.ConnectionString;
    private readonly string _serverBConn;
    private readonly bool _skip;

    public RedisPerTenantConnectionTests(SecondRedisServerFixture serverB)
    {
        _serverBConn = serverB.ConnectionString!;
        _skip = serverB.ConnectionString == null;
    }

    [Fact]
    public async Task tenant_message_is_published_over_the_tenants_own_connection()
    {
        if (_skip) return;

        // Same stream key on both servers — the tenant's dedicated multiplexer, not any key rewriting, is
        // what isolates the two.
        var streamKey = $"pertenant-{Guid.NewGuid():N}";

        using var host = await BuildSenderAsync(streamKey);

        await host.MessageBus().SendAsync(new RedisBrokerMessage("for-tenant-b"),
            new DeliveryOptions { TenantId = "tenantB" });

        // Landed on server B (the tenant's dedicated connection)...
        var onB = await RedisMultiBrokerHelpers.WaitForStreamAsync(_serverBConn, streamKey, 15.Seconds());
        onB.Length.ShouldBe(1);

        // ...and NOT on the shared server A.
        var onA = await RedisMultiBrokerHelpers.ReadStreamAsync(_serverAConn, streamKey);
        onA.Length.ShouldBe(0);
    }

    [Fact]
    public async Task default_message_uses_the_shared_connection()
    {
        if (_skip) return;

        var streamKey = $"pertenant-{Guid.NewGuid():N}";

        using var host = await BuildSenderAsync(streamKey);

        await host.MessageBus().SendAsync(new RedisBrokerMessage("no-tenant"));

        var onA = await RedisMultiBrokerHelpers.WaitForStreamAsync(_serverAConn, streamKey, 15.Seconds());
        onA.Length.ShouldBe(1);

        var onB = await RedisMultiBrokerHelpers.ReadStreamAsync(_serverBConn, streamKey);
        onB.Length.ShouldBe(0);
    }

    [Fact]
    public async Task tenant_message_is_consumed_over_the_tenants_own_connection()
    {
        if (_skip) return;

        var streamKey = $"pertenant-{Guid.NewGuid():N}";

        // A single host both publishes and listens. The tenant listener runs on server B (the dedicated
        // connection), so a message tagged for tenantB round-trips back through it, stamped with the tenant id.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantInbound";
                opts.UseRedisTransport(_serverAConn)
                    .AutoProvision()
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenantB", _serverBConn);

                opts.PublishMessage<RedisBrokerMessage>().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "tenant-group");
            })
            .StartAsync();

        var session = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<RedisBrokerMessage>(host)
            .ExecuteAndWaitAsync(c =>
                c.SendAsync(new RedisBrokerMessage("for-tenant-b"), new DeliveryOptions { TenantId = "tenantB" }));

        var received = session.Received.SingleEnvelope<RedisBrokerMessage>();
        received.TenantId.ShouldBe("tenantB");
        received.Message.ShouldBeOfType<RedisBrokerMessage>().Id.ShouldBe("for-tenant-b");
    }

    private Task<IHost> BuildSenderAsync(string streamKey)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantSender";
                opts.UseRedisTransport(_serverAConn)
                    .AutoProvision()
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    // The tenant gets its own dedicated connection to server B.
                    .AddTenant("tenantB", _serverBConn);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<RedisBrokerMessage>().ToRedisStream(streamKey).SendInline();
            })
            .StartAsync();
    }
}
