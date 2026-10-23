using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Sqlite;
using Wolverine.Tracking;

namespace SqliteTests;

/// <summary>
/// GH-5127. SQLite message storage registered by connection-string name (the way Aspire's CommunityToolkit
/// AddSqlite(...) hands a project its database), and with static tenant databases resolved by name.
/// </summary>
public class named_connection_5127
{
    // The shape of the value CommunityToolkit.Aspire.Hosting.Sqlite's SqliteResource publishes
    private static string aspireStyle(SqliteTestDatabase database) =>
        $"Data Source={database.DatabaseFile};Cache=Shared;Mode=ReadWriteCreate";

    private static IHostBuilder hostWith(Action<WolverineOptions> configure,
        params (string Name, string Value)[] connectionStrings)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                connectionStrings.Select(x =>
                    new KeyValuePair<string, string?>($"ConnectionStrings:{x.Name}", x.Value))))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                configure(opts);
            });
    }

    [Fact]
    public async Task a_missing_name_fails_startup_naming_the_message_store()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts =>
                opts.PersistMessagesWithSqliteUsingNamedConnection("wolverinedb", "named5127"))
                .StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Single().Name.ShouldBe("wolverinedb");
        ex.Message.ShouldContain("required by the SQLite message store");
        ex.Message.ShouldContain("builder.AddSqlite(\"wolverinedb\")");
    }

    [Fact]
    public async Task a_configured_name_is_used_for_the_message_store()
    {
        using var database = Servers.CreateDatabase(nameof(named_connection_5127));
        var connectionString = aspireStyle(database);

        #region sample_sqlite_persistence_using_named_connection

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Under Aspire, builder.AddSqlite("wolverinedb") (CommunityToolkit.Aspire.Hosting.Sqlite) plus
                // .WithReference(...) in the AppHost supplies this
                ["ConnectionStrings:wolverinedb"] = connectionString
            }))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Resolve the "wolverinedb" connection string from IConfiguration
                opts.PersistMessagesWithSqliteUsingNamedConnection("wolverinedb", "named5127");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        #endregion

        var store = host.GetRuntime().Storage.ShouldBeAssignableTo<IMessageDatabase>()!;
        store.Settings.ConnectionString.ShouldBe(connectionString);
        store.SchemaName.ShouldBe("named5127");

        // Really talks to the database
        (await host.GetRuntime().Storage.Admin.FetchCountsAsync()).ShouldNotBeNull();

        // The DatabaseSettings registered for EF Core customization carries the resolved value too
        host.Services.GetRequiredService<DatabaseSettings>().ConnectionString.ShouldBe(connectionString);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_in_memory_value_is_still_rejected()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await hostWith(opts =>
                    opts.PersistMessagesWithSqliteUsingNamedConnection("wolverinedb", "named5127"),
                ("wolverinedb", "Data Source=:memory:")).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain("in-memory databases are not supported");
        ex.Message.ShouldContain("'wolverinedb'");
    }

    [Fact]
    public async Task static_tenant_databases_can_be_resolved_by_name()
    {
        using var mainDatabase = Servers.CreateDatabase(nameof(named_connection_5127) + "_main");
        using var tenantDatabase = Servers.CreateDatabase(nameof(named_connection_5127) + "_tenant1");
        var mainConnectionString = aspireStyle(mainDatabase);
        var tenantConnectionString = aspireStyle(tenantDatabase);

        #region sample_sqlite_static_tenants_using_named_connections

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:wolverinedb"] = mainConnectionString,
                ["ConnectionStrings:tenant1db"] = tenantConnectionString
            }))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PersistMessagesWithSqliteUsingNamedConnection("wolverinedb", "named5127")
                    .RegisterStaticTenantsUsingNamedConnections(tenants =>
                    {
                        // tenant id, then the name of its connection string
                        tenants.Register("tenant1", "tenant1db");
                    });
            })
            .StartAsync(TestContext.Current.CancellationToken);

        #endregion

        var store = host.GetRuntime().Storage.ShouldBeOfType<MultiTenantedMessageStore>();
        var tenantStore = await store.GetDatabaseAsync("tenant1");
        tenantStore.ShouldNotBeNull();
        (await tenantStore.Admin.FetchCountsAsync()).ShouldNotBeNull();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_missing_tenant_name_fails_startup_naming_the_tenant()
    {
        using var database = Servers.CreateDatabase(nameof(named_connection_5127));

        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts =>
                    opts.PersistMessagesWithSqliteUsingNamedConnection("wolverinedb", "named5127")
                        .RegisterStaticTenantsUsingNamedConnections(t => t.Register("tenant1", "tenant1db")),
                ("wolverinedb", database.ConnectionString)).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Single().Name.ShouldBe("tenant1db");
        ex.Message.ShouldContain("message store (tenant 'tenant1')");
    }
}
