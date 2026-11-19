using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests.Sagas;

/// <summary>
/// GH-5665. The wolverine_saga_history table exists only once some saga records its history, is added to an existing
/// message store by the usual migration, and is cleared with the rest of the store's data.
/// </summary>
public class saga_history_table_provisioning_5665 : PostgresqlContext
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PostgresqlMessageStore storeFor(string schemaName, bool recordSagaHistory)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.PostgresConnectionString,
            SchemaName = schemaName,
            Role = MessageStoreRole.Main
        };

        return new PostgresqlMessageStore(settings, new DurabilitySettings { RecordSagaHistory = recordSagaHistory },
            NpgsqlDataSource.Create(Servers.PostgresConnectionString), NullLogger<PostgresqlMessageStore>.Instance);
    }

    private static async Task<long> tableCountAsync(string schemaName)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from information_schema.tables where table_schema = '{schemaName}' and table_name = '{DatabaseConstants.SagaHistoryTable}'";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task the_table_is_not_created_when_no_saga_records_history()
    {
        await using var store = storeFor("saga_history_off", false);
        await store.Admin.MigrateAsync();

        (await tableCountAsync("saga_history_off")).ShouldBe(0);
    }

    [Fact]
    public async Task an_existing_store_gains_the_table_and_clearing_the_store_empties_it()
    {
        const string schema = "saga_history_on";

        await using (var before = storeFor(schema, false))
        {
            await before.Admin.MigrateAsync();
        }

        await using var store = storeFor(schema, true);
        await store.Admin.MigrateAsync();
        (await tableCountAsync(schema)).ShouldBe(1);

        var history = (ISagaHistoryStore)store;
        var sagaId = Guid.NewGuid().ToString();
        await history.RecordSagaHistoryAsync(new SagaHistoryRecord
        {
            SagaType = "Some.Saga",
            SagaId = sagaId,
            MessageType = "some-message",
            Handler = "Saga.Handle",
            Outcome = SagaHistoryOutcome.Started
        }, Ct);

        (await history.ReadSagaHistoryAsync("Some.Saga", sagaId, null, 0, 10, Ct)).TotalCount.ShouldBe(1);

        await store.Admin.ClearAllAsync();

        (await history.ReadSagaHistoryAsync("Some.Saga", sagaId, null, 0, 10, Ct)).TotalCount.ShouldBe(0);
    }
}
