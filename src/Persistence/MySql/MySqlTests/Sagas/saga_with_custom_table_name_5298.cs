using IntegrationTests;
using JasperFx.Resources;
using MySqlConnector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.MySql;
using Wolverine.Tracking;

namespace MySqlTests.Sagas;

// GH-5298: the generated saga code built its schema with the convention table name and ignored the
// name given to AddSagaType<T>(tableName)
[Collection("mysql")]
public class saga_with_custom_table_name_5298
{
    private const string SchemaName = "saga_5298";

    [Fact]
    public async Task saga_state_is_written_to_the_registered_table()
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = $"DROP DATABASE IF EXISTS {SchemaName}; CREATE DATABASE {SchemaName};";
            await dropCmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<CustomTableSaga5298>();
                opts.AddSagaType<CustomTableSaga5298>("my_own_saga_table");
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, SchemaName);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartCustomTableSaga5298(id));

        await using var connection = new MySqlConnection(Servers.MySqlConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"select count(*) from {SchemaName}.my_own_saga_table where id = @id";
        cmd.Parameters.AddWithValue("@id", id);
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
