using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Npgsql;
using JasperFx.Resources;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests.Sagas;

public class configuring_saga_table_storage : PostgresqlContext
{
    [Fact]
    public async Task add_tables_to_persistence()
    {
        await dropSchemaAsync();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.AddSagaType<RedSaga>("red");
                opts.AddSagaType<BlueSaga>("blue");
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "color_sagas");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
        
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        (await new Table(new DbObjectName("color_sagas", "red")).ExistsInDatabaseAsync(conn)).ShouldBeTrue();
        (await new Table(new DbObjectName("color_sagas", "blue")).ExistsInDatabaseAsync(conn)).ShouldBeTrue();
    }

    private static async Task dropSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("color_sagas");
        await conn.CloseAsync();
    }
}

public class RedSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

public class BlueSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}