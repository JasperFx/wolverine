using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace MartenTests.MultiTenancy;

/// <summary>
/// With a database per tenant, runtime.Storage is a <see cref="MultiTenantedMessageStore" />. It has to hand
/// logical deduplication to its main store, as it already does recurring messages; left at the interface's
/// no-op default, every [Deduplicated] chain threw on its first keyed message.
/// </summary>
public class logical_deduplication_with_a_database_per_tenant : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var tenant1 = await databaseFor(conn, "tenant1");
        var tenant2 = await databaseFor(conn, "tenant2");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ChargeTenantHandler))
                    .IncludeType(typeof(RecordTenantChargeHandler));

                // GH-4813 follow up. The shape that actually bit the reporter: a chain that commits
                // through a Marten session. Without it the suite only covered the non-transactional
                // chain, which reaches the compensating path without ever consulting
                // TryBuildTransactionalDeduplication -- so the guard that sends a database-per-tenant
                // chain down that path could break and every test here would stay green.
                opts.Policies.AutoApplyTransactions();

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
                opts.Durability.DeduplicationWindow = 1.Hours();

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.DatabaseSchemaName = "mt_dedup";
                        m.MultiTenantedDatabases(tenancy =>
                        {
                            tenancy.AddSingleTenantDatabase(tenant1, "tenant1");
                            tenancy.AddSingleTenantDatabase(tenant2, "tenant2");
                        });
                    })
                    .IntegrateWithWolverine(m =>
                    {
                        m.MessageStorageSchemaName = "dedup_control";
                        m.MainDatabaseConnectionString = Servers.PostgresConnectionString;
                    });

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await _host.ResetResourceState();
        ChargeTenantHandler.Received.Clear();
        RecordTenantChargeHandler.Received.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static async Task<string> databaseFor(NpgsqlConnection conn, string name)
    {
        if (!await conn.DatabaseExists(name))
        {
            await new DatabaseSpecification().BuildDatabase(conn, name);
        }

        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString) { Database = name }.ConnectionString;
    }

    [Fact]
    public void the_claims_are_kept_in_the_main_store()
    {
        var storage = _host.GetRuntime().Storage;
        var main = storage.ShouldBeOfType<MultiTenantedMessageStore>().Main;

        storage.Deduplication.ShouldBeSameAs(main.Deduplication);
        storage.Deduplication.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task a_replay_of_the_same_id_for_a_tenant_is_discarded()
    {
        await _host.SendMessageAndWaitAsync(new ChargeTenant("first"),
            new DeliveryOptions { TenantId = "tenant1", DeduplicationId = "invoice-17" });
        await _host.SendMessageAndWaitAsync(new ChargeTenant("second"),
            new DeliveryOptions { TenantId = "tenant1", DeduplicationId = "invoice-17" });

        ChargeTenantHandler.Received.ShouldHaveSingleItem().ShouldBe("first");
    }

    [Fact]
    public async Task an_id_is_shared_by_every_tenant_because_the_claim_is_in_the_main_store()
    {
        await _host.SendMessageAndWaitAsync(new ChargeTenant("tenant1"),
            new DeliveryOptions { TenantId = "tenant1", DeduplicationId = "invoice-18" });
        await _host.SendMessageAndWaitAsync(new ChargeTenant("tenant2"),
            new DeliveryOptions { TenantId = "tenant2", DeduplicationId = "invoice-18" });

        ChargeTenantHandler.Received.ShouldHaveSingleItem().ShouldBe("tenant1");
    }

    /// <summary>
    /// GH-4813 follow up. A transactional chain cannot queue its claim onto the session's unit of work
    /// here -- the claims live in the main database and the session commits to a tenant one, so
    /// MartenPersistenceFrameProvider refuses the transactional path on anything but Single cardinality
    /// and falls through to claim-and-release. That fallthrough is what makes this work, and nothing
    /// pinned it: if the cardinality guard ever went away, this shape would reach
    /// MartenDeduplicator.tableFor with a MultiTenantedMessageStore and throw the very exception #4813
    /// removed, while every other test in this class stayed green.
    /// </summary>
    [Fact]
    public async Task a_transactional_chain_deduplicates_too()
    {
        await _host.SendMessageAndWaitAsync(new RecordTenantCharge("first"),
            new DeliveryOptions { TenantId = "tenant1", DeduplicationId = "invoice-19" });
        await _host.SendMessageAndWaitAsync(new RecordTenantCharge("second"),
            new DeliveryOptions { TenantId = "tenant1", DeduplicationId = "invoice-19" });

        RecordTenantChargeHandler.Received.ShouldHaveSingleItem().ShouldBe("first");
    }
}

public record ChargeTenant(string Note);

public static class ChargeTenantHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    public static void Handle(ChargeTenant message)
    {
        lock (Received)
        {
            Received.Add(message.Note);
        }
    }
}

public record RecordTenantCharge(string Note);

/// <summary>
/// Takes an IDocumentSession, so with AutoApplyTransactions this chain commits through Marten -- the
/// shape <see cref="logical_deduplication_with_a_database_per_tenant.a_transactional_chain_deduplicates_too" />
/// exists to pin.
/// </summary>
public static class RecordTenantChargeHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    public static void Handle(RecordTenantCharge message, IDocumentSession session)
    {
        session.Store(new TenantCharge { Id = Guid.NewGuid(), Note = message.Note });

        lock (Received)
        {
            Received.Add(message.Note);
        }
    }
}

public class TenantCharge
{
    public Guid Id { get; set; }
    public string Note { get; set; } = string.Empty;
}
