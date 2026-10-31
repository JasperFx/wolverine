using IntegrationTests;
using JasperFx.Resources;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace PostgresqlTests.Sagas;

// GH-5298: the generated saga code built its schema with the convention table name and ignored the
// name given to AddSagaType<T>(tableName)
public class saga_with_custom_table_name_5298
{
    private const string SchemaName = "saga_5298";

    [Fact]
    public async Task saga_state_is_written_to_the_registered_table()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(SchemaName, TestContext.Current.CancellationToken);
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<CustomTableSaga5298>();
                opts.AddSagaType<CustomTableSaga5298>("myownsagatable");
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartCustomTableSaga5298(id));

        await using var connection = new NpgsqlConnection(Servers.PostgresConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"select count(*) from {SchemaName}.myownsagatable where id = :id";
        cmd.Parameters.AddWithValue(":id", id);
        ((long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(1);

        // And nothing was created under the convention name
        (await new Table(new DbObjectName(SchemaName, "customtablesaga5298_saga"))
            .ExistsInDatabaseAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }
}

public record StartCustomTableSaga5298(Guid Id);

public class CustomTableSaga5298 : Saga
{
    public Guid Id { get; set; }

    public static CustomTableSaga5298 Start(StartCustomTableSaga5298 command) => new() { Id = command.Id };
}
