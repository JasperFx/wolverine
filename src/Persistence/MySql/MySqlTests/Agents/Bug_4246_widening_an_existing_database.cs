using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime.Agents;
using Xunit;

namespace MySqlTests.Agents;

/// <summary>
/// GH-4246, the other half: an application already running against MySQL has a
/// <c>wolverine_node_records</c> table with the defaulted <c>VARCHAR(255)</c> description, and the whole
/// point of the fix is that it stops failing without anyone hand-editing the schema. Weasel's MySQL
/// table delta turns a widened column into an in-place <c>ALTER TABLE ... MODIFY COLUMN</c>, so the
/// existing rows come through untouched -- this is what pins that.
///
/// Requires Weasel 9.29.1 or later. Before that the schema differ stripped the size off a column type
/// before comparing, so a widened varchar was invisible to it and this test fails at 255.
/// </summary>
[Collection("mysql")]
public class Bug_4246_widening_an_existing_database : IAsyncLifetime
{
    private const string SchemaName = "node_record_widening";

    public async ValueTask InitializeAsync()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();

        await executeAsync(conn, $"DROP DATABASE IF EXISTS `{SchemaName}`");
        await executeAsync(conn, $"CREATE DATABASE `{SchemaName}`");

        // The table exactly as Wolverine 6.32 provisioned it -- description defaulted to VARCHAR(255).
        await executeAsync(conn, $"""
            CREATE TABLE `{SchemaName}`.`wolverine_node_records` (
                `id` INT NOT NULL AUTO_INCREMENT,
                `node_number` INT NOT NULL,
                `event_name` VARCHAR(255) NOT NULL,
                `timestamp` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
                `description` VARCHAR(255) NULL,
                CONSTRAINT `pkey_wolverine_node_records_id` PRIMARY KEY (`id`)
            )
            """);

        await executeAsync(conn,
            $"INSERT INTO `{SchemaName}`.`wolverine_node_records` (node_number, event_name, description) " +
            "VALUES (1, 'NodeStarted', 'an existing row')");

        await conn.CloseAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task executeAsync(MySqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task migrating_widens_the_description_in_place_and_keeps_the_rows()
    {
        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.MySqlConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        await using var store = new MySqlMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<MySqlMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await store.Admin.MigrateAsync();

        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using (var widthCommand = conn.CreateCommand())
        {
            widthCommand.CommandText =
                "select character_maximum_length from information_schema.columns " +
                $"where table_schema = '{SchemaName}' and table_name = 'wolverine_node_records' " +
                "and column_name = 'description'";
            Convert.ToInt64(await widthCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))
                .ShouldBe(NodeRecord.DescriptionLength);
        }

        // MODIFY COLUMN, not a rebuild: the diagnostics that were already there are still there.
        await using (var rowCommand = conn.CreateCommand())
        {
            rowCommand.CommandText =
                $"select description from `{SchemaName}`.`wolverine_node_records` where node_number = 1";
            (await rowCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))
                .ShouldBe("an existing row");
        }

        await conn.CloseAsync();
    }
}
