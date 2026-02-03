using IntegrationTests;
using MySqlConnector;
using Shouldly;
using Weasel.MySql;
using Wolverine;
using Wolverine.MySql.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace MySqlTests.Sagas;

[Collection("mysql")]
public class saga_storage_operations
{
    private readonly DatabaseSagaSchema<MySqlLightweightSaga, Guid> theSchema;

    public saga_storage_operations()
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.MySqlConnectionString,
            SchemaName = "lightweight_sagas",
        };

        var definition = new SagaTableDefinition(typeof(MySqlLightweightSaga), null);
        theSchema = new DatabaseSagaSchema<MySqlLightweightSaga, Guid>(definition, settings);
    }

    [Fact]
    public async Task load_with_no_document_happily_returns_null()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();

        using var tx = await conn.BeginTransactionAsync();

        var saga = await theSchema.LoadAsync(Guid.NewGuid(), tx, CancellationToken.None);
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task get_an_argument_out_of_range_exception_for_missing_id()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await using var db = await conn.BeginTransactionAsync();

        var saga = new MySqlLightweightSaga
        {
            Id = Guid.Empty,
            Name = "Xavier Worthy",
        };

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await theSchema.InsertAsync(saga, db, CancellationToken.None);
        });
    }

    [Fact]
    public async Task insert_then_load()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await using var db = await conn.BeginTransactionAsync();

        var saga = new MySqlLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        using var db2 = await conn.BeginTransactionAsync();
        var saga2 = await theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);

        saga2.ShouldNotBeNull();
        saga2.Name.ShouldBe("Xavier Worthy");
    }

    [Fact]
    public async Task insert_update_then_load()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await using var db = await conn.BeginTransactionAsync();

        var saga = new MySqlLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);

        saga.Name = "Hollywood Brown";
        await theSchema.UpdateAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        using var db2 = await conn.BeginTransactionAsync();
        var saga2 = await theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);

        saga2.ShouldNotBeNull();
        saga2.Name.ShouldBe("Hollywood Brown");
    }

    [Fact]
    public async Task insert_then_delete()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await using var db = await conn.BeginTransactionAsync();

        var saga = new MySqlLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);

        await theSchema.DeleteAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        using var db2 = await conn.BeginTransactionAsync();
        var saga2 = await theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);
        saga2.ShouldBeNull();
    }

    [Fact]
    public async Task concurrency_exception_when_version_does_not_match()
    {
        await theSchema.EnsureStorageExistsAsync(CancellationToken.None);

        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();

        // Clean up the table
        var cleanCmd = conn.CreateCommand();
        cleanCmd.CommandText = "DELETE FROM lightweight_sagas.mysqllightweightsaga_saga";
        await cleanCmd.ExecuteNonQueryAsync();

        var db = await conn.BeginTransactionAsync();

        var saga = new MySqlLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();
        await db.DisposeAsync();

        db = await conn.BeginTransactionAsync();

        saga.Name = "Rashee Rice";
        await theSchema.UpdateAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();
        await db.DisposeAsync();

        db = await conn.BeginTransactionAsync();

        // I'm rewinding the version to make it throw
        saga.Version = 1;

        await Should.ThrowAsync<SagaConcurrencyException>(async () =>
        {
            await theSchema.UpdateAsync(saga, db, CancellationToken.None);
            await db.CommitAsync();
            await db.DisposeAsync();
        });
    }
}

public class MySqlLightweightSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
