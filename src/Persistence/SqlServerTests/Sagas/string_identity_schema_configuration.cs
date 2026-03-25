using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.SqlServer.Sagas;

namespace SqlServerTests.Sagas;

public class string_identity_schema_configuration
{
    [Fact]
    public void add_saga_type_defaults_string_ids_to_varchar_mapping()
    {
        var options = new WolverineOptions();
        options.AddSagaType<StringIdentitySaga>();

        using var services = options.Services.BuildServiceProvider();
        var definition = services.GetServices<SagaTableDefinition>().Single();

        definition.UseNVarCharForStringId.ShouldBeFalse();
    }

    [Fact]
    public void add_saga_type_can_opt_into_nvarchar_mapping_for_string_ids()
    {
        var options = new WolverineOptions();
        options.AddSagaType<StringIdentitySaga>(useNVarCharForStringId: true);

        using var services = options.Services.BuildServiceProvider();
        var definition = services.GetServices<SagaTableDefinition>().Single();

        definition.UseNVarCharForStringId.ShouldBeTrue();
    }

    [Fact]
    public void sql_server_saga_schema_defaults_string_ids_to_varchar()
    {
        var table = BuildSchema().Table.ShouldBeOfType<Table>();

        table.Columns.Single(x => x.Name == "id").Type.ShouldBe("varchar(100)");
        table.PrimaryKeyColumns.Single().ShouldBe("id");
    }

    [Fact]
    public void sql_server_saga_schema_can_opt_into_nvarchar_for_string_ids()
    {
        var table = BuildSchema(useNVarCharForStringId: true).Table.ShouldBeOfType<Table>();

        table.Columns.Single(x => x.Name == "id").Type.ShouldBe("nvarchar(100)");
        table.PrimaryKeyColumns.Single().ShouldBe("id");
    }

    [Fact]
    public async Task can_create_nvarchar_table_and_delta_is_none()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("string_sagas");

        var table = BuildSchema(useNVarCharForStringId: true).Table.ShouldBeOfType<Table>();

        await table.MigrateAsync(conn);

        var delta = await table.FindDeltaAsync(conn);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact(Skip = "Requires Weasel fix to drop/re-add PK before ALTER COLUMN on SQL Server")]
    public async Task can_migrate_varchar_to_nvarchar_and_delta_is_none()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("string_sagas");

        // Step 1: create with default varchar(100)
        var varcharTable = BuildSchema(useNVarCharForStringId: false).Table.ShouldBeOfType<Table>();
        await varcharTable.MigrateAsync(conn);

        var initialDelta = await varcharTable.FindDeltaAsync(conn);
        initialDelta.Difference.ShouldBe(SchemaPatchDifference.None);

        // Step 2: switch to nvarchar(100) and migrate
        var nvarcharTable = BuildSchema(useNVarCharForStringId: true).Table.ShouldBeOfType<Table>();
        await nvarcharTable.MigrateAsync(conn);

        // Step 3: verify no remaining delta
        var finalDelta = await nvarcharTable.FindDeltaAsync(conn);
        finalDelta.Difference.ShouldBe(SchemaPatchDifference.None);
    }

    internal static DatabaseSagaSchema<string, StringIdentitySaga> BuildSchema(bool useNVarCharForStringId = false)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString,
            SchemaName = "string_sagas",
        };

        var definition = new SagaTableDefinition(typeof(StringIdentitySaga), null, useNVarCharForStringId);
        return new DatabaseSagaSchema<string, StringIdentitySaga>(definition, settings);
    }
}

public class StringIdentitySaga : Saga
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = null!;
}