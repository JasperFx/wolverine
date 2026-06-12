using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Events;
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

// Coverage for the VIPLive zorgdeclaraties production combination that exposed marten#4727 under
// the actual Wolverine-managed daemon: a multi-stage composite projection whose stage-2 member
// publishes side-effect messages (RaiseSideEffects -> slice.PublishMessage), on a
// Conjoined + Quick + UseTenantPartitionedEvents store with
// UseWolverineManagedEventSubscriptionDistribution = true.
//
// The optimized composite rebuild runs in ShardExecutionMode.Continuous, so the stage-2 side
// effects fire and the parallel event slices contend on ProjectionUpdateBatch.CurrentMessageBatch.
// Before the semaphore-release fix that path leaked the semaphore and the composite never caught
// up (the daemon froze, as observed in production); this test asserts the composite materializes
// for every managed tenant under Wolverine-managed distribution.
public partial class composite_side_effects_under_managed_distribution(ITestOutputHelper output)
    : PostgresqlContext, IAsyncLifetime
{
    private readonly string theSchema = "csp_se_" + Guid.NewGuid().ToString("N");
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = theSchema;

                        m.Events.StreamIdentity = StreamIdentity.AsString;
                        m.Events.TenancyStyle = TenancyStyle.Conjoined;
                        m.Events.AppendMode = EventAppendMode.Quick;
                        m.Events.UseTenantPartitionedEvents = true;

                        m.Projections.CompositeProjectionFor("CsComposite", c =>
                        {
                            c.Add<CsTripProjection>(stageNumber: 1);
                            c.Add<CsTripNoticeProjection>(stageNumber: 2);
                        });

                        m.Schema.For<CsTrip>().DocumentAlias("cs_trip");
                        m.Schema.For<CsTripNotice>().DocumentAlias("cs_notice");
                    })
                    .IntegrateWithWolverine(m => m.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(output));
                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.AddMartenManagedTenantsAsync(default, new Dictionary<string, string>
        {
            ["t1"] = "t1",
            ["t2"] = "t2",
            [StorageConstants.DefaultTenantId] = "default"
        });
    }

    public async Task DisposeAsync()
    {
        _host.GetRuntime().Agents.DisableHealthChecks();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task composite_with_side_effects_catches_up_under_managed_distribution()
    {
        await _host.WaitUntilAssumesLeadershipAsync(15.Seconds());

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        const int perTenant = 20;
        var tenants = new[] { "t1", "t2" };

        foreach (var tenant in tenants)
        {
            await using var session = store.LightweightSession(tenant);
            for (var i = 0; i < perTenant; i++)
            {
                var key = Guid.NewGuid().ToString();
                session.Events.StartStream<CsTrip>(key, new TripStarted(key), new TripLeg(1.0), new TripLeg(2.5));
            }

            await session.SaveChangesAsync();
        }

        // The managed daemon must catch the composite up for every tenant. Before the #4727 fix the
        // side-effect-publishing slices deadlock on the message-batch semaphore and these documents
        // never materialize (the wait times out).
        using var cts = new CancellationTokenSource(90.Seconds());
        foreach (var tenant in tenants)
        {
            await waitForCountAsync<CsTrip>(store, tenant, perTenant, cts.Token);
            await waitForCountAsync<CsTripNotice>(store, tenant, perTenant, cts.Token);
        }
    }

    private static async Task waitForCountAsync<T>(IDocumentStore store, string tenant, int expected, CancellationToken token)
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

    public record TripStarted(string Id);

    public record TripLeg(double Distance);

    public record TripNoticed(string Id);

    public class CsTrip
    {
        public string Id { get; set; } = null!;
        public double Distance { get; set; }
    }

    public partial class CsTripProjection: SingleStreamProjection<CsTrip, string>
    {
        public CsTripProjection() => Name = "CsTrip";
        public void Apply(CsTrip agg, TripLeg e) => agg.Distance += e.Distance;
    }

    public class CsTripNotice
    {
        public string Id { get; set; } = null!;
        public int Legs { get; set; }
    }

    public partial class CsTripNoticeProjection: SingleStreamProjection<CsTripNotice, string>
    {
        public CsTripNoticeProjection() => Name = "CsTripNotice";
        public void Apply(CsTripNotice agg, TripLeg e) => agg.Legs++;

        public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<CsTripNotice> slice)
        {
            slice.PublishMessage(new TripNoticed(slice.Events()[0].StreamKey!));
            return new ValueTask();
        }
    }
}
