using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.Distribution;

// Phase 2 of #3021 (subscriptions slice): PublishEventsToWolverine under Conjoined + Quick +
// UseTenantPartitionedEvents. Under conjoined tenancy the relay binds RelayWithEventTenant (GH-2675),
// so each event reaches its Wolverine handler attributed with the event's own per-row TenantId — the
// tenant lives in the row, not the database. This pins that an event appended in tenant1 is delivered
// with TenantId "tenant1", tenant2 with "tenant2", and a default-tenant event with the literal
// StorageConstants.DefaultTenantId (the case GH-2675 fixed against silent misrouting to the database id).
// Also exercises the event-as-message (IEvent<T>) handler shape.
public class tenant_partitioned_subscription_attribution : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private readonly string theSchema = "tpe_sub_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        SubscribedCounterHandler.TenantByAmount.Clear();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = theSchema;

                        m.Events.StreamIdentity = StreamIdentity.AsString;
                        m.Events.TenancyStyle = TenancyStyle.Conjoined;
                        m.Events.AppendMode = EventAppendMode.Quick;
                        m.Events.UseTenantPartitionedEvents = true;
                    })
                    .IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .PublishEventsToWolverine("tenant_attribution", relay => relay.PublishEvent<CounterChanged>());

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SubscribedCounterHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await theStore.Advanced.AddMartenManagedTenantsAsync(default, new Dictionary<string, string>
        {
            ["tenant1"] = "tenant1",
            ["tenant2"] = "tenant2",
            [StorageConstants.DefaultTenantId] = "default"
        });
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task relayed_events_carry_their_own_per_row_tenant()
    {
        var daemon = await theStore.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        // Typed as Func<…, Task> to disambiguate the Task/ValueTask ExecuteAndWaitAsync overloads.
        Func<IMessageContext, Task> append = async _ =>
        {
            await Append("tenant1", 5);
            await Append("tenant2", 11);
            await Append(StorageConstants.DefaultTenantId, 7);
            await daemon.WaitForNonStaleData(30.Seconds());
        };

        await theHost.TrackActivity().Timeout(60.Seconds()).ExecuteAndWaitAsync(append);

        SubscribedCounterHandler.TenantByAmount[5].ShouldBe("tenant1");
        SubscribedCounterHandler.TenantByAmount[11].ShouldBe("tenant2");
        SubscribedCounterHandler.TenantByAmount[7].ShouldBe(StorageConstants.DefaultTenantId);
    }

    private async Task Append(string tenant, int amount)
    {
        await using var session = theStore.LightweightSession(tenant);
        session.Events.StartStream("c-" + Guid.NewGuid().ToString("N"), new CounterChanged(amount));
        await session.SaveChangesAsync();
    }
}

// Event-as-message handler: receives the relayed IEvent<CounterChanged> and records the tenant the
// envelope was delivered under, keyed by the (unique-per-tenant) amount.
public static class SubscribedCounterHandler
{
    public static readonly ConcurrentDictionary<int, string?> TenantByAmount = new();

    public static void Handle(IEvent<CounterChanged> e, Envelope envelope)
        => TenantByAmount[e.Data.Amount] = envelope.TenantId;
}
