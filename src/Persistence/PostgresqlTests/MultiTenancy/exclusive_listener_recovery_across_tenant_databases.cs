using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.ComplianceTests.ExclusiveListeners;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace PostgresqlTests.MultiTenancy;

/// <summary>
/// GH-3590, separate-database-per-tenant variant. A durable exclusive listener owns its own inbox recovery, and
/// "its own inbox" means <em>every</em> database that can hold rows for it — the main store AND every tenant
/// database — not just the default one. <see cref="ListenerInboxRecovery"/> goes through
/// <c>MessageStoreCollection.FindAllAsync()</c> for exactly this reason.
/// </summary>
public class exclusive_listener_recovery_across_tenant_databases : PostgresqlContext, IAsyncLifetime
{
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private readonly string[] theTenants = ["red", "blue", "green"];

    private IHost _host = null!;

    private string MainSchema => $"ex3590_{theSuffix}";
    private string TenantDatabase(string tenant) => $"w3590_{tenant}_{theSuffix}";

    private static string ConnectionStringFor(string database)
    {
        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = database
        }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();

            foreach (var tenant in theTenants)
            {
                var databaseName = TenantDatabase(tenant);
                if (!await conn.DatabaseExists(databaseName))
                {
                    await new DatabaseSpecification().BuildDatabase(conn, databaseName);
                }
            }

            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.PersistMessagesWithPostgresql(ConnectionStringFor("postgres"), MainSchema)
                    .RegisterStaticTenants(tenants =>
                    {
                        foreach (var tenant in theTenants)
                        {
                            tenants.Register(tenant, ConnectionStringFor(TenantDatabase(tenant)));
                        }
                    });

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<RecoveredMessageHandler>();

                opts.ListenToSingleNodeEndpoint(
                    ExclusiveListenerRecoveryCompliance.ExclusiveEndpointName, ListenerScope.Exclusive);
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task drains_dormant_messages_out_of_every_tenant_database()
    {
        using var tracking = RecoveredMessages.Track();

        var runtime = _host.GetRuntime();
        var destination =
            SingleNodeListenerTransport.ToUri(ExclusiveListenerRecoveryCompliance.ExclusiveEndpointName);

        runtime.Endpoints.FindListeningAgent(destination)
            .ShouldNotBeNull("The exclusive listener should be running in Solo mode");

        var expected = new List<Guid>();

        // Seed dormant rows in EVERY tenant database, not just the main one
        foreach (var tenant in theTenants)
        {
            var store = await runtime.Stores.Main.As<MultiTenantedMessageStore>().Source.FindAsync(tenant);
            store.ShouldNotBeNull();

            var seeded = await seedAsync(store!, runtime, destination, tenant, 3);
            expected.AddRange(seeded);
        }

        var succeeded = await waitForAsync(() => expected.All(tracking.Contains), 60.Seconds());

        succeeded.ShouldBeTrue(
            $"Expected all {expected.Count} dormant messages across {theTenants.Length} tenant databases to be " +
            $"recovered by the exclusive listener, but only saw {tracking.Count}");
    }

    private static async Task<Guid[]> seedAsync(IMessageStore store, IWolverineRuntime runtime, Uri destination,
        string tenantId, int count)
    {
        var serializer = runtime.Options.DefaultSerializer;

        var envelopes = Enumerable.Range(0, count).Select(i =>
        {
            var id = Guid.NewGuid();
            var envelope = new Envelope(new RecoveredMessage(id, i))
            {
                Id = id,
                Destination = destination,
                Status = EnvelopeStatus.Incoming,
                OwnerId = TransportConstants.AnyNode,
                ContentType = serializer.ContentType,
                MessageType = typeof(RecoveredMessage).ToMessageTypeName(),
                TenantId = tenantId,
                SentAt = DateTimeOffset.UtcNow
            };

            envelope.Data = serializer.Write(envelope);

            return envelope;
        }).ToArray();

        await store.Inbox.StoreIncomingAsync(envelopes);

        return envelopes.Select(x => x.Id).ToArray();
    }

    private static async Task<bool> waitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(100.Milliseconds());
        }

        return condition();
    }
}
