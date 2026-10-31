using JasperFx.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Sqlite;
using Wolverine.Tracking;
using Wolverine.RDBMS;

namespace SqliteTests.Sagas;

// GH-5298: the generated saga code built its schema with the convention table name and ignored the
// name given to AddSagaType<T>(tableName)
public class saga_with_custom_table_name_5298
{
    [Fact]
    public async Task saga_state_is_written_to_the_registered_table()
    {
        using var database = Servers.CreateDatabase("saga_5298");

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<CustomTableSaga5298>();
                opts.AddSagaType<CustomTableSaga5298>("MyOwnSagaTable");
                opts.UseSqlitePersistenceAndTransport(database.ConnectionString).AutoProvision();
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartCustomTableSaga5298(id));

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "select count(*) from MyOwnSagaTable where id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        ((long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(1);

        // And nothing was created under the convention name
        await using var exists = connection.CreateCommand();
        exists.CommandText = "select count(*) from sqlite_master where name = 'customtablesaga5298_saga'";
        ((long)(await exists.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(0);
    }
}

public record StartCustomTableSaga5298(Guid Id);

public class CustomTableSaga5298 : Saga
{
    public Guid Id { get; set; }

    public static CustomTableSaga5298 Start(StartCustomTableSaga5298 command) => new() { Id = command.Id };
}
