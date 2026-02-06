using IntegrationTests;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using JasperFx.Resources;
using Shouldly;
using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine;
using Wolverine.MySql;
using Wolverine.RDBMS;
using MySqlTests.MultiTenancy;

namespace MySqlTests.Sagas;

[Collection("mysql")]
public class configuring_saga_table_storage
{
    [Fact]
    public async Task add_tables_to_persistence()
    {
        await dropSchemaAsync();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.AddSagaType<MySqlRedSaga>("red");
                opts.AddSagaType<MySqlBlueSaga>("blue");
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "color_sagas");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();

        // Check that the saga tables exist
        var redTable = new Table(new DbObjectName("color_sagas", "red"));
        (await redTable.ExistsInDatabaseAsync(conn)).ShouldBeTrue();

        var blueTable = new Table(new DbObjectName("color_sagas", "blue"));
        (await blueTable.ExistsInDatabaseAsync(conn)).ShouldBeTrue();

        await conn.CloseAsync();
        await host.StopAsync();
    }

    private static async Task dropSchemaAsync()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();

        // Drop and recreate the schema
        var dropCmd = conn.CreateCommand();
        dropCmd.CommandText = "DROP DATABASE IF EXISTS color_sagas; CREATE DATABASE color_sagas;";
        await dropCmd.ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }
}
