using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Core;
using Weasel.MySql;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace MySqlTests;

[Collection("mysql")]
public class SchemaTests
{
    [Fact]
    public void check_all_objects_are_yielded()
    {
        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = "receiver",
            CommandQueuesEnabled = true,
            Role = MessageStoreRole.Main
        };
        var durabilitySettings = new DurabilitySettings();
        var store = new MySqlMessageStore(settings, durabilitySettings, dataSource,
            NullLogger<MySqlMessageStore>.Instance);

        var objects = store.AllObjects().ToList();
        var objectNames = objects.Select(o => o.Identifier.Name).ToList();

        // Print all objects for debugging
        foreach (var name in objectNames)
        {
            Console.WriteLine($"Object: {name}");
        }

        // These should all be created
        objectNames.ShouldContain("wolverine_outgoing_envelopes");
        objectNames.ShouldContain("wolverine_incoming_envelopes");
        objectNames.ShouldContain("wolverine_dead_letters");
        objectNames.ShouldContain("wolverine_nodes");
        objectNames.ShouldContain("wolverine_node_assignments");
        objectNames.ShouldContain("wolverine_control_queue");
        objectNames.ShouldContain("wolverine_node_records");
        objectNames.ShouldContain("wolverine_agent_restrictions");
    }

    [Fact]
    public async Task diagnose_table_ddl()
    {
        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = "receiver",
            CommandQueuesEnabled = true,
            Role = MessageStoreRole.Main
        };
        var durabilitySettings = new DurabilitySettings();
        var store = new MySqlMessageStore(settings, durabilitySettings, dataSource,
            NullLogger<MySqlMessageStore>.Instance);

        var migrator = new MySqlMigrator();

        Console.WriteLine("=== DDL for each table ===\n");

        foreach (var obj in store.AllObjects())
        {
            Console.WriteLine($"--- {obj.Identifier.QualifiedName} ---");
            var writer = new StringWriter();
            obj.WriteCreateStatement(migrator, writer);
            Console.WriteLine(writer.ToString());
            Console.WriteLine();
        }
    }

    [Fact]
    public async Task try_create_each_table_individually()
    {
        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = "receiver",
            CommandQueuesEnabled = true,
            Role = MessageStoreRole.Main
        };
        var durabilitySettings = new DurabilitySettings();
        var store = new MySqlMessageStore(settings, durabilitySettings, dataSource,
            NullLogger<MySqlMessageStore>.Instance);

        var migrator = new MySqlMigrator();

        // First, ensure database exists and drop all existing tables
        await using var setupConn = await dataSource.OpenConnectionAsync();
        await using var setupCmd = setupConn.CreateCommand();
        setupCmd.CommandText = "CREATE DATABASE IF NOT EXISTS `receiver`";
        await setupCmd.ExecuteNonQueryAsync();

        // Drop existing tables
        setupCmd.CommandText = @"
            SET FOREIGN_KEY_CHECKS = 0;
            DROP TABLE IF EXISTS receiver.wolverine_node_assignments;
            DROP TABLE IF EXISTS receiver.wolverine_control_queue;
            DROP TABLE IF EXISTS receiver.wolverine_node_records;
            DROP TABLE IF EXISTS receiver.wolverine_nodes;
            DROP TABLE IF EXISTS receiver.wolverine_agent_restrictions;
            DROP TABLE IF EXISTS receiver.wolverine_incoming_envelopes;
            DROP TABLE IF EXISTS receiver.wolverine_outgoing_envelopes;
            DROP TABLE IF EXISTS receiver.wolverine_dead_letters;
            SET FOREIGN_KEY_CHECKS = 1;";
        await setupCmd.ExecuteNonQueryAsync();
        await setupConn.CloseAsync();

        Console.WriteLine("=== Trying to create each table ===\n");

        foreach (var obj in store.AllObjects())
        {
            Console.WriteLine($"--- Creating {obj.Identifier.QualifiedName} ---");
            var writer = new StringWriter();
            obj.WriteCreateStatement(migrator, writer);
            var sql = writer.ToString();
            Console.WriteLine($"SQL:\n{sql}");

            try
            {
                await using var conn = await dataSource.OpenConnectionAsync();

                // Execute each statement separately
                var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var statement in statements)
                {
                    var trimmed = statement.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = trimmed;
                    await cmd.ExecuteNonQueryAsync();
                }

                await conn.CloseAsync();
                Console.WriteLine("SUCCESS\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}\n");
            }
        }

        // Show what tables exist now
        await using var checkConn = await dataSource.OpenConnectionAsync();
        await using var checkCmd = checkConn.CreateCommand();
        checkCmd.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'receiver'";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        Console.WriteLine("\nTables that exist:");
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"  - {reader.GetString(0)}");
        }
    }

    [Fact]
    public async Task can_migrate_schema_directly()
    {
        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = "receiver",
            CommandQueuesEnabled = true,
            Role = MessageStoreRole.Main
        };
        var durabilitySettings = new DurabilitySettings();
        var store = new MySqlMessageStore(settings, durabilitySettings, dataSource,
            NullLogger<MySqlMessageStore>.Instance);

        Console.WriteLine($"Store Role: {store.Role}");
        Console.WriteLine("Objects before migration:");
        foreach (var obj in store.AllObjects())
        {
            Console.WriteLine($"  - {obj.Identifier.QualifiedName}");
        }

        // Should migrate without error
        try
        {
            await store.Admin.MigrateAsync();
            Console.WriteLine("Migration completed successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Migration failed: {ex}");
            throw;
        }

        // Verify tables exist in the receiver database
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'receiver'";
        await using var reader = await cmd.ExecuteReaderAsync();
        Console.WriteLine("\nTables after migration in receiver database:");
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"  - {reader.GetString(0)}");
        }
        await conn.CloseAsync();

        // Assert all expected tables exist
        var expectedTables = new[]
        {
            "wolverine_nodes",
            "wolverine_node_assignments",
            "wolverine_node_records",
            "wolverine_control_queue",
            "wolverine_agent_restrictions",
            "wolverine_incoming_envelopes",
            "wolverine_outgoing_envelopes",
            "wolverine_dead_letters"
        };

        await using var conn2 = await dataSource.OpenConnectionAsync();
        await using var cmd2 = conn2.CreateCommand();
        cmd2.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'receiver'";
        await using var reader2 = await cmd2.ExecuteReaderAsync();
        var actualTables = new List<string>();
        while (await reader2.ReadAsync())
        {
            actualTables.Add(reader2.GetString(0));
        }

        foreach (var table in expectedTables)
        {
            actualTables.ShouldContain(table, $"Missing table: {table}");
        }
    }
}
