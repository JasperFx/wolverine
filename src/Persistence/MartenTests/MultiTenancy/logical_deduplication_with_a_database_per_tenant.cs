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
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ChargeTenantHandler));

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
