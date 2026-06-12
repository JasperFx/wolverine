using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MartenTests.Distribution.Support;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

// Regression guard for a composite projection catching up under the Wolverine-hosted daemon
// (IntegrateWithWolverine + UseWolverineManagedEventSubscriptionDistribution) with
// UseTenantPartitionedEvents and multiple managed tenants.
//
// This reproduced a stall when Wolverine consumed a Marten version predating the high-water
// timestamp fix for tenant-partitioned events (marten#4712): the store-global HighWaterAgent read a
// default(DateTimeOffset) = 0001-01-01 timestamp, computed a bogus safe-harbor, and never advanced.
// The composite finished a premature "0 to 1" optimized rebuild, went continuous, and was never told
// to catch up to the real high-water — while a plain SingleStreamProjection (which happened to consume
// the first good mark) caught up fine. On Marten >= 9.7.3 the high-water mark advances and the
// composite catches up like Marten's own daemon does.
public partial class composite_catches_up_under_managed_distribution_with_partitioning(ITestOutputHelper output)
    : PostgresqlContext, IAsyncLifetime
{
    private readonly string theSchema = "cs_part_" + Guid.NewGuid().ToString("N");
    private readonly string[] _tenants = ["tenant_alpha", "tenant_beta"];
    private const int StreamsPerTenant = 10;
    private IHost _host = null!;

    private void ConfigureMarten(StoreOptions m)
    {
        m.DisableNpgsqlLogging = true;
        m.Connection(Servers.PostgresConnectionString);
        m.DatabaseSchemaName = theSchema;

        m.Events.TenancyStyle = TenancyStyle.Conjoined;
        m.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
        m.Events.UseTenantPartitionedEvents = true;
        m.Policies.AllDocumentsAreMultiTenanted();

        m.Projections.Add<CsPlainProjection>(ProjectionLifecycle.Async);
        m.Projections.CompositeProjectionFor("CsComposite", c =>
        {
            c.Add<CsTripProjection>(stageNumber: 1);
            c.Add<CsTripNoticeProjection>(stageNumber: 2);
        });

        m.Schema.For<CsPlain>().DocumentAlias("cs_plain");
        m.Schema.For<CsTrip>().DocumentAlias("cs_trip");
        m.Schema.For<CsTripNotice>().DocumentAlias("cs_notice");
    }

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

                opts.Services.AddMarten(ConfigureMarten)
                    .IntegrateWithWolverine(m => m.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(output));
                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.AddMartenManagedTenantsAsync(default, new Dictionary<string, string>
        {
            ["tenant_alpha"] = "tenant_alpha",
            ["tenant_beta"] = "tenant_beta",
            [StorageConstants.DefaultTenantId] = "default"
        });

        foreach (var tenant in _tenants)
        {
            await using var session = store.LightweightSession(tenant);
            for (var i = 0; i < StreamsPerTenant; i++)
            {
                var id = Guid.NewGuid();
                session.Events.StartStream<CsTrip>(id, new TripStarted(id), new TripLeg(1.0), new TripLeg(2.5));
            }

            await session.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        _host.GetRuntime().Agents.DisableHealthChecks();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task composite_catches_up_for_every_partitioned_tenant()
    {
        await _host.WaitUntilAssumesLeadershipAsync(15.Seconds());

        var store = _host.Services.GetRequiredService<IDocumentStore>();

        using var cts = new CancellationTokenSource(90.Seconds());
        foreach (var tenant in _tenants)
        {
            await WaitForCountAsync<CsPlain>(store, tenant, StreamsPerTenant, cts.Token);
            await WaitForCountAsync<CsTrip>(store, tenant, StreamsPerTenant, cts.Token);
            await WaitForCountAsync<CsTripNotice>(store, tenant, StreamsPerTenant, cts.Token);
        }
    }

    private static async Task WaitForCountAsync<T>(IDocumentStore store, string tenant, int expected, CancellationToken token)
        where T : notnull
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await using (var session = store.QuerySession(tenant))
            {
                if (await session.Query<T>().CountAsync(token) >= expected)
                {
                    return;
                }
            }

            await Task.Delay(500, token);
        }
    }

    public record TripStarted(Guid Id);

    public record TripLeg(double Distance);

    public class CsPlain
    {
        public Guid Id { get; set; }
        public double Distance { get; set; }
    }

    public partial class CsPlainProjection: SingleStreamProjection<CsPlain, Guid>
    {
        public CsPlainProjection() => Name = "CsPlain";
        public CsPlain Create(TripStarted e) => new() { Id = e.Id };
        public void Apply(CsPlain agg, TripLeg e) => agg.Distance += e.Distance;
    }

    public class CsTrip
    {
        public Guid Id { get; set; }
        public double Distance { get; set; }
    }

    public partial class CsTripProjection: SingleStreamProjection<CsTrip, Guid>
    {
        public CsTripProjection() => Name = "CsTrip";
        public CsTrip Create(TripStarted e) => new() { Id = e.Id };
        public void Apply(CsTrip agg, TripLeg e) => agg.Distance += e.Distance;
    }

    public class CsTripNotice
    {
        public Guid Id { get; set; }
        public int Legs { get; set; }
    }

    public partial class CsTripNoticeProjection: SingleStreamProjection<CsTripNotice, Guid>
    {
        public CsTripNoticeProjection() => Name = "CsTripNotice";
        public CsTripNotice Create(TripStarted e) => new() { Id = e.Id };
        public void Apply(CsTripNotice agg, TripLeg e) => agg.Legs++;
    }
}
