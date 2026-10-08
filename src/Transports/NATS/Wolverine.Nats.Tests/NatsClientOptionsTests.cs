using System.Threading.Channels;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Nats.Internal;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// <c>ConfigureNatsOpts()</c> is the escape hatch for NATS.Net client settings the transport configuration does not
/// surface -- the subscription pending channel, ping interval, reconnect behavior -- for the shared connection and
/// for every tenant's dedicated connection alike.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsClientOptionsTests
{
    private readonly NatsContainerFixture _fixture;

    public NatsClientOptionsTests(NatsContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task configure_nats_opts_reaches_shared_and_tenant_connections()
    {
        var namesSeenByTheHook = new List<string>();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NatsOptsHook";
                opts.UseNats(_fixture.ConnectionString)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    // Added before the hook, so its connection configuration was copied without it
                    .AddTenant("tenant-a", cfg => cfg.ConnectionString = _fixture.ConnectionString)
                    .ConfigureNatsOpts(o =>
                    {
                        lock (namesSeenByTheHook)
                        {
                            namesSeenByTheHook.Add(o.Name);
                        }

                        return o with
                        {
                            SubPendingChannelCapacity = 4321,
                            SubPendingChannelFullMode = BoundedChannelFullMode.Wait,
                            PingInterval = 17.Seconds()
                        };
                    })
                    // A tenant can replace the transport's hook with its own
                    .AddTenant("tenant-b", cfg =>
                    {
                        cfg.ConnectionString = _fixture.ConnectionString;
                        cfg.ConfigureNatsOpts = o => o with { SubPendingChannelCapacity = 99 };
                    });
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<NatsTransport>();

        var shared = transport.Connection.Opts;
        shared.SubPendingChannelCapacity.ShouldBe(4321);
        shared.SubPendingChannelFullMode.ShouldBe(BoundedChannelFullMode.Wait);
        shared.PingInterval.ShouldBe(17.Seconds());

        var tenantA = transport.Tenants["tenant-a"].Connection.ShouldNotBeNull().Opts;
        tenantA.SubPendingChannelCapacity.ShouldBe(4321);
        tenantA.SubPendingChannelFullMode.ShouldBe(BoundedChannelFullMode.Wait);
        tenantA.PingInterval.ShouldBe(17.Seconds());

        var tenantB = transport.Tenants["tenant-b"].Connection.ShouldNotBeNull().Opts;
        tenantB.SubPendingChannelCapacity.ShouldBe(99);
        tenantB.PingInterval.ShouldBe(2.Minutes());

        // The hook runs after Wolverine has named each connection, so it can rely on (or replace) the name...
        namesSeenByTheHook.ShouldBe(["wolverine-NatsOptsHook", "wolverine-nats-tenant-tenant-a"],
            ignoreOrder: true);

        // ...and what it leaves alone is still Wolverine's
        shared.Name.ShouldBe("wolverine-NatsOptsHook");
        shared.Url.ShouldBe(_fixture.ConnectionString);
        tenantA.Name.ShouldBe("wolverine-nats-tenant-tenant-a");
    }

    [Fact]
    public async Task connection_defaults_are_unchanged_without_the_hook()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NatsOptsDefaults";
                opts.UseNats(_fixture.ConnectionString);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var opts = host.GetRuntime().Options.Transports.GetOrCreate<NatsTransport>().Connection.Opts;

        opts.SubPendingChannelCapacity.ShouldBe(1024);
        opts.SubPendingChannelFullMode.ShouldBe(BoundedChannelFullMode.DropNewest);
        opts.PingInterval.ShouldBe(2.Minutes());
        opts.MaxPingOut.ShouldBe(2);
        opts.Name.ShouldBe("wolverine-NatsOptsDefaults");
    }
}
