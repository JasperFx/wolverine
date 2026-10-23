using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.MySql;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Xunit;

namespace MySqlTests;

/// <summary>
/// GH-5127. MySQL message storage registered by connection-string name (the way Aspire hands a project its
/// database), by a MySqlDataSource from the container (Aspire.MySqlConnector AddMySqlDataSource), and with static tenant
/// databases resolved by name.
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
                opts.PersistMessagesWithMySqlUsingNamedConnection("wolverinedb", "named5127")).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Single().Name.ShouldBe("wolverinedb");
        ex.Message.ShouldContain("required by the MySQL message store");
        ex.Message.ShouldContain("builder.AddMySql(\"mysql\").AddDatabase(\"wolverinedb\")");
    }

    [Fact]
    public async Task a_configured_name_is_used_for_the_message_store()
    {
        #region sample_mysql_persistence_using_named_connection

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Under Aspire, builder.AddMySql("server").AddDatabase("wolverinedb") plus
                // .WithReference(...) in the AppHost supplies this
                ["ConnectionStrings:wolverinedb"] = Servers.MySqlConnectionString
            }))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Resolve the "wolverinedb" connection string from IConfiguration
                opts.PersistMessagesWithMySqlUsingNamedConnection("wolverinedb", "named5127");
            })
            .UseResourceSetupOnStartup()
            .StartAsync(TestContext.Current.CancellationToken);

        #endregion

        var store = host.GetRuntime().Storage.ShouldBeAssignableTo<IMessageDatabase>()!;
        new MySqlConnectionStringBuilder(store.Settings.ConnectionString!).Database
            .ShouldBe(new MySqlConnectionStringBuilder(Servers.MySqlConnectionString).Database);
        store.SchemaName.ShouldBe("named5127");

        // Really talks to the database
        (await host.GetRuntime().Storage.Admin.FetchCountsAsync()).ShouldNotBeNull();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_data_source_can_come_from_the_container()
    {
        await using var dataSource = new MySqlDataSourceBuilder(Servers.MySqlConnectionString).Build();

        using (var host = await Host.CreateDefaultBuilder()
                   .UseWolverine(opts =>
                   {
                       opts.Durability.Mode = DurabilityMode.Solo;

                       #region sample_mysql_persistence_using_data_source_from_container

                       // e.g. registered by Aspire's builder.AddMySqlDataSource("wolverinedb")
                       opts.Services.AddSingleton(dataSource);

                       opts.PersistMessagesWithMySqlUsingDataSourceFromContainer(schemaName: "named5127");

                       #endregion
                   })
                   .UseResourceSetupOnStartup()
                   .StartAsync(TestContext.Current.CancellationToken))
        {
            var store = host.GetRuntime().Storage.ShouldBeAssignableTo<IMessageDatabase>()!;
            store.DataSource.ShouldBeSameAs(dataSource);
            (await host.GetRuntime().Storage.Admin.FetchCountsAsync()).ShouldNotBeNull();

            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // The container owns the data source, so Wolverine must not have disposed it
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        connection.State.ShouldBe(System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task static_tenant_databases_can_be_resolved_by_name()
    {
        #region sample_mysql_static_tenants_using_named_connections

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:wolverinedb"] = Servers.MySqlConnectionString,
                ["ConnectionStrings:tenant1db"] = Servers.MySqlConnectionString
            }))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PersistMessagesWithMySqlUsingNamedConnection("wolverinedb", "named5127")
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
                    opts.PersistMessagesWithMySqlUsingNamedConnection("wolverinedb", "named5127")
                        .RegisterStaticTenantsUsingNamedConnections(t => t.Register("tenant1", "tenant1db")),
                ("wolverinedb", Servers.MySqlConnectionString)).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Single().Name.ShouldBe("tenant1db");
        ex.Message.ShouldContain("message store (tenant 'tenant1')");
    }
}
