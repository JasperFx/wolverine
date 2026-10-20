using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests.Bugs;

/// <summary>
/// MessageDatabase.DisposeAsync disposed its data source unconditionally, including one the caller passed to
/// PersistMessagesWithPostgresql(NpgsqlDataSource) or registered as a tenant database. Disposing an
/// NpgsqlDataSource aborts every physical connection rented from it, so stopping a host broke everything else
/// still using that data source. Wolverine now disposes only a data source it built itself.
/// </summary>
public class Bug_message_store_disposes_data_source_it_does_not_own : PostgresqlContext
{
    private const string SchemaName = "ds_ownership";

    [Fact]
    public async Task stopping_the_host_does_not_dispose_a_caller_supplied_data_source()
    {
        await using var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithPostgresql(dataSource, SchemaName);
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        await shouldStillBeUsable(dataSource);
    }

    [Fact]
    public async Task stopping_the_host_does_not_dispose_a_caller_supplied_tenant_data_source()
    {
        var tenantConnectionString = await createDatabaseIfNotExists("ds_ownership_tenant");

        await using var tenantDataSource = NpgsqlDataSource.Create(tenantConnectionString);

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName)
                    .RegisterStaticTenantsByDataSource(tenants => tenants.Register("red", tenantDataSource));
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        await shouldStillBeUsable(tenantDataSource);
    }

    [Fact]
    public async Task stopping_the_host_disposes_a_data_source_wolverine_built()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
            }).StartAsync(TestContext.Current.CancellationToken);

        var dataSource = host.Services.GetRequiredService<IMessageStore>().ShouldBeAssignableTo<IMessageDatabase>()!
            .DataSource;

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(async () =>
        {
            await using var conn = await dataSource.OpenConnectionAsync();
        });
    }

    private static async Task shouldStillBeUsable(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("select 1");
        (await command.ExecuteScalarAsync()).ShouldBe(1);
    }

    private static async Task<string> createDatabaseIfNotExists(string databaseName)
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();

            if (!await conn.DatabaseExists(databaseName))
            {
                await new DatabaseSpecification().BuildDatabase(conn, databaseName);
            }
        }

        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString) { Database = databaseName }
            .ConnectionString;
    }
}
