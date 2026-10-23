using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Oracle;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Xunit;

namespace OracleTests;

/// <summary>
/// GH-5127. Oracle message storage registered by connection-string name (the way Aspire's
/// AddOracle("server").AddDatabase("name") hands a project its database), and with static tenant databases
/// resolved by name. There is no data-source-from-the-container form: Wolverine.Oracle takes no data source, and
/// Aspire's only Oracle client integration is the EF Core one.
/// </summary>
[Collection("oracle")]
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
                opts.PersistMessagesWithOracleUsingNamedConnection("wolverinedb", "named5127")).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Single().Name.ShouldBe("wolverinedb");
        ex.Message.ShouldContain("required by the Oracle message store");
        ex.Message.ShouldContain("builder.AddOracle(\"oracle\").AddDatabase(\"wolverinedb\")");
    }

    [Fact]
    public async Task a_configured_name_is_used_for_the_message_store()
    {
        #region sample_oracle_persistence_using_named_connection

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Under Aspire, builder.AddOracle("server").AddDatabase("wolverinedb") plus
                // .WithReference(...) in the AppHost supplies this
                ["ConnectionStrings:wolverinedb"] = Servers.OracleConnectionString
            }))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Resolve the "wolverinedb" connection string from IConfiguration
                opts.PersistMessagesWithOracleUsingNamedConnection("wolverinedb", "named5127");
            })
            .UseResourceSetupOnStartup()
            .StartAsync(TestContext.Current.CancellationToken);

        #endregion

        var store = host.GetRuntime().Storage.ShouldBeAssignableTo<IMessageDatabase>()!;
        store.Settings.ConnectionString.ShouldBe(Servers.OracleConnectionString);
        store.SchemaName.ShouldBe("NAMED5127");

        // Really talks to the database
        (await host.GetRuntime().Storage.Admin.FetchCountsAsync()).ShouldNotBeNull();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task static_tenant_databases_can_be_resolved_by_name()
    {
        #region sample_oracle_static_tenants_using_named_connections

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:wolverinedb"] = Servers.OracleConnectionString,
                ["ConnectionStrings:tenant1db"] = Servers.OracleConnectionString
            }))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PersistMessagesWithOracleUsingNamedConnection("wolverinedb", "named5127")
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
                    opts.PersistMessagesWithOracleUsingNamedConnection("wolverinedb", "named5127")
                        .RegisterStaticTenantsUsingNamedConnections(t => t.Register("tenant1", "tenant1db")),
                ("wolverinedb", Servers.OracleConnectionString)).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Single().Name.ShouldBe("tenant1db");
        ex.Message.ShouldContain("message store (tenant 'tenant1')");
    }
}
