using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

/// <summary>
///     GH-4485. On a single-database store, WolverineSubscriptionRunner used to stamp
///     <c>IMartenDatabase.Identifier</c> onto the subscription's MessageContext as the tenant id. For a
///     single database that identifier is <c>StoreOptions.StoreName</c>, which DEFAULTS to "Main" -- not a
///     tenant id at all. It rode out on every envelope the subscription published, propagated to cascading
///     messages, and OutboxedSessionFactory then opened the downstream handler's Marten session for tenant
///     "Main". Marten's DefaultTenancy accepts any tenant id without complaint on a single-database store,
///     so the handler appended events stamped <c>tenant_id = 'Main'</c> next to data written as
///     <c>*DEFAULT*</c> -- and under EventAppendMode.Quick the tenant guard inside
///     <c>mt_quick_append_events</c> then failed every append to a pre-existing stream with
///     "P0001: The tenantid does not match the existing stream".
/// </summary>
public class Bug_4485_subscription_does_not_stamp_store_name_as_tenant : PostgresqlContext
{
    private static async Task dropSchema()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("bug4485");
        await conn.CloseAsync();
    }

    private static async Task<IHost> startHostAsync()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(RecordSightingHandler));
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "bug4485";
                        m.DisableNpgsqlLogging = true;

                        // The tenant guard in mt_quick_append_events is what turned this from a silently
                        // mis-stamped tenant_id into a hard production failure
                        m.Events.AppendMode = EventAppendMode.Quick;
                    })
                    .IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .PublishEventsToWolverine("bug4485", relay => relay.PublishEvent<CritterSighted>())
                    .AddAsyncDaemon(DaemonMode.Solo);
            })
            .StartAsync();
    }

    private static Func<IMessageContext, Task> startTheStream(IDocumentStore store, Guid streamId)
    {
        return async _ =>
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(streamId, new CritterSighted("possum"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        };
    }

    [Fact]
    public async Task subscription_published_messages_carry_no_tenant_id_on_a_single_database_store()
    {
        await dropSchema();

        using var host = await startHostAsync();
        var store = host.Services.GetRequiredService<IDocumentStore>();

        var streamId = Guid.NewGuid();

        var tracked = await host
            .TrackActivity()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<IEvent<CritterSighted>>(host)
            .ExecuteAndWaitAsync(startTheStream(store, streamId));

        // Before the fix this was "Main" -- StoreOptions.StoreName, not a tenant
        tracked.Received.SingleEnvelope<IEvent<CritterSighted>>()
            .TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task downstream_handler_appends_to_the_pre_existing_default_tenant_stream()
    {
        await dropSchema();

        using var host = await startHostAsync();
        var store = host.Services.GetRequiredService<IDocumentStore>();

        var streamId = Guid.NewGuid();

        // Establish the stream on the default tenant first, exactly like data written before the
        // upgrade. The subscription-driven handler then has to append to THIS stream.
        var tracked = await host
            .TrackActivity()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<IEvent<CritterSighted>>(host)
            .ExecuteAndWaitAsync(startTheStream(store, streamId));

        // On the old code the handler's append blew up with
        // "P0001: The tenantid does not match the existing stream", which the tracked session
        // asserts on all by itself.
        tracked.Received.SingleEnvelope<IEvent<CritterSighted>>().ShouldNotBeNull();

        await using var query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken);

        events.Select(x => x.EventType).ShouldContain(typeof(SightingRecorded));
        events.Select(x => x.TenantId).Distinct()
            .ShouldHaveSingleItem()
            .ShouldBe(StorageConstants.DefaultTenantId);
    }

    public record CritterSighted(string Species);

    public record SightingRecorded(string Species);

    public static class RecordSightingHandler
    {
        // Appends back to the same stream through the outboxed session, which is where the bogus
        // "Main" tenant id landed
        public static async Task Handle(IEvent<CritterSighted> e, IDocumentSession session)
        {
            session.Events.Append(e.StreamId, new SightingRecorded(e.Data.Species));
            await session.SaveChangesAsync();
        }
    }
}
