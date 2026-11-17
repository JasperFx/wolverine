using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RDBMS;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace SqlServerTests.Sagas;

// GH-5572: a lightweight saga table created by Wolverine 6.x has no completed_at column. Starting 7.0 against it adds
// the column, and the sagas already in it carry on and can then be retained.
public class saga_retention_migration_from_6x_5572
{
    private const string SchemaName = "saga_retention_6x";
    private const string TableName = "retained_saga_6x";

    [Fact]
    public async Task a_6x_saga_table_gets_completed_at_and_its_sagas_carry_on()
    {
        var token = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString();

        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(token);
            await conn.DropSchemaAsync(SchemaName, token);
            await conn.CreateCommand($"create schema {SchemaName}").ExecuteNonQueryAsync(token);

            // The table exactly as Wolverine 6.x created it, holding one open saga written by 6.x
            await conn.CreateCommand($"""
                create table {SchemaName}.{TableName} (
                    id varchar(100) not null primary key,
                    body varbinary(max) not null,
                    version int not null default 1,
                    created datetimeoffset not null default SYSDATETIMEOFFSET(),
                    last_modified datetimeoffset not null default SYSDATETIMEOFFSET()
                )
                """).ExecuteNonQueryAsync(token);

            var insert = conn.CreateCommand($"insert into {SchemaName}.{TableName} (id, body) values (@id, @body)");
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("body",
                System.Text.Encoding.UTF8.GetBytes($$"""{"Id":"{{id}}","Touches":0,"Version":1}"""));
            await insert.ExecuteNonQueryAsync(token);
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<RetainedSaga>();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.AddSagaType<RetainedSaga>(TableName);
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(token);

        await using var connection = new SqlConnection(Servers.SqlServerConnectionString);
        await connection.OpenAsync(token);

        var column = connection.CreateCommand(
            "select is_nullable from information_schema.columns where table_schema = @schema and table_name = @table and column_name = 'completed_at'");
        column.Parameters.AddWithValue("schema", SchemaName);
        column.Parameters.AddWithValue("table", TableName);
        (await column.ExecuteScalarAsync(token)).ShouldBe("YES");

        // The 6.x saga is still open, and handled as before
        await host.InvokeMessageAndWaitAsync(new TouchRetainedSaga(id));
        await host.InvokeMessageAndWaitAsync(new FinishRetainedSaga(id));

        var completedAt = connection.CreateCommand($"select completed_at from {SchemaName}.{TableName} where id = @id");
        completedAt.Parameters.AddWithValue("id", id);
        (await completedAt.ExecuteScalarAsync(token)).ShouldBeOfType<DateTimeOffset>();

        var state = await host.GetRuntime().SagaStorage.ReadSagaAsync(typeof(RetainedSaga).FullName!, id, token);
        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeTrue();
    }
}
