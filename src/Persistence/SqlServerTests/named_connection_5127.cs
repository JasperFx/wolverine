using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace SqlServerTests;

/// <summary>
/// GH-5127. SQL Server message storage registered by connection-string name (the way Aspire hands a project its
/// database), and with static tenant databases resolved by name.
/// </summary>
public class named_connection_5127
{
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
                opts.PersistMessagesWithSqlServerUsingNamedConnection("wolverinedb", "named5127")).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Single().Name.ShouldBe("wolverinedb");
        ex.Message.ShouldContain("required by the SQL Server message store");
        ex.Message.ShouldContain("builder.AddSqlServer(\"wolverinedb\")");
    }

    [Fact]
    public async Task a_configured_name_is_used_for_the_message_store()
    {
        #region sample_sqlserver_persistence_using_named_connection

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Under Aspire, builder.AddSqlServer("server").AddDatabase("wolverinedb") plus
                // .WithReference(...) in the AppHost supplies this
                ["ConnectionStrings:wolverinedb"] = Servers.SqlServerConnectionString
            }))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Resolve the "wolverinedb" connection string from IConfiguration
                opts.PersistMessagesWithSqlServerUsingNamedConnection("wolverinedb", "named5127");
            })
            .UseResourceSetupOnStartup()
            .StartAsync(TestContext.Current.CancellationToken);

        #endregion

        var store = host.GetRuntime().Storage.ShouldBeAssignableTo<IMessageDatabase>()!;
        new SqlConnectionStringBuilder(store.Settings.ConnectionString).InitialCatalog
            .ShouldBe(new SqlConnectionStringBuilder(Servers.SqlServerConnectionString).InitialCatalog);
        store.SchemaName.ShouldBe("named5127");

        // The DatabaseSettings registered for EF Core and friends sees the resolved value too
        host.Services.GetRequiredService<DatabaseSettings>().ConnectionString
            .ShouldBe(Servers.SqlServerConnectionString);

        // Really talks to the database
        (await host.GetRuntime().Storage.Admin.FetchCountsAsync()).ShouldNotBeNull();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task static_tenant_databases_can_be_resolved_by_name()
    {
        #region sample_sqlserver_static_tenants_using_named_connections

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:wolverinedb"] = Servers.SqlServerConnectionString,
                ["ConnectionStrings:tenant1db"] = Servers.SqlServerConnectionString
            }))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PersistMessagesWithSqlServerUsingNamedConnection("wolverinedb", "named5127")
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

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_missing_tenant_name_fails_startup_naming_the_tenant()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts =>
                    opts.PersistMessagesWithSqlServerUsingNamedConnection("wolverinedb", "named5127")
                        .RegisterStaticTenantsUsingNamedConnections(t => t.Register("tenant1", "tenant1db")),
                ("wolverinedb", Servers.SqlServerConnectionString)).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Single().Name.ShouldBe("tenant1db");
        ex.Message.ShouldContain("message store (tenant 'tenant1')");
    }
}
