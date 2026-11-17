using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Sqlite;
using Wolverine.Tracking;

namespace SqliteTests.Sagas;

// GH-5572: a SQLite saga table created by Wolverine 6.x has no completed_at column. A saga type that was never
// registered with AddSagaType() has its table checked lazily, inside the saga's own transaction, which used to only
// create a missing table. It now adds the column too, so the sagas already in it carry on and can be retained.
public class saga_retention_migration_from_6x_5572
{
    [Fact]
    public async Task a_6x_saga_table_gets_completed_at_and_its_sagas_carry_on()
    {
        var token = TestContext.Current.CancellationToken;
        using var database = Servers.CreateDatabase(nameof(saga_retention_migration_from_6x_5572));
        var id = Guid.NewGuid().ToString();

        await using (var conn = new SqliteConnection(database.ConnectionString))
        {
            await conn.OpenAsync(token);

            // The table exactly as Wolverine 6.x created it, holding one open saga written by 6.x
            var create = conn.CreateCommand();
            create.CommandText = """
                create table retainedsaga_saga (
                    id TEXT not null primary key,
                    body TEXT not null,
                    version INTEGER not null default 1,
                    created TEXT not null default (datetime('now')),
                    last_modified TEXT not null default (datetime('now'))
                )
                """;
            await create.ExecuteNonQueryAsync(token);

            var insert = conn.CreateCommand();
            insert.CommandText = "insert into retainedsaga_saga (id, body) values (@id, @body)";
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@body", $$"""{"Id":"{{id}}","Touches":0,"Version":1}""");
            await insert.ExecuteNonQueryAsync(token);
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<RetainedSaga>();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithSqlite(database.ConnectionString);
            }).StartAsync(token);

        // The 6.x saga is still open, and handled as before
        await host.InvokeMessageAndWaitAsync(new TouchRetainedSaga(id));
        await host.InvokeMessageAndWaitAsync(new FinishRetainedSaga(id));

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        var completedAt = connection.CreateCommand();
        completedAt.CommandText = "select completed_at from retainedsaga_saga where id = @id";
        completedAt.Parameters.AddWithValue("@id", id);
        (await completedAt.ExecuteScalarAsync(token)).ShouldBeOfType<string>();

        await host.StopAsync(token);
    }
}
