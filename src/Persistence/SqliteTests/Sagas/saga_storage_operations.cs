using Microsoft.Data.Sqlite;
using Shouldly;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Sqlite.Sagas;

namespace SqliteTests.Sagas;

public class saga_storage_operations : SqliteContext, IAsyncLifetime
{
    private readonly DatabaseSagaSchema<LightweightSaga, Guid> _theSchema;
    private readonly string _connectionString;
    private SqliteConnection? _keepAlive;

    public saga_storage_operations()
    {
        _connectionString = Servers.CreateInMemoryConnectionString();

        var settings = new DatabaseSettings
        {
            ConnectionString = _connectionString,
            SchemaName = "main",
        };

        var definition = new SagaTableDefinition(typeof(LightweightSaga), null);
        _theSchema = new DatabaseSagaSchema<LightweightSaga, Guid>(definition, settings);
    }

    public async Task InitializeAsync()
    {
        // Open keep-alive connection for the in-memory database
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        // Create the saga table before any tests run (avoids locking issues
        // when ensureStorageExistsAsync opens a second connection while a transaction is open)
        var table = (Table)_theSchema.Table;
        var sql = table.ToBasicCreateTableSql();
        var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
        _theSchema.MarkAsChecked();
    }

    public async Task DisposeAsync()
    {
        if (_keepAlive != null)
        {
            await _keepAlive.CloseAsync();
            await _keepAlive.DisposeAsync();
        }
    }

    [Fact]
    public async Task load_with_no_document_happily_returns_null()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var tx = await conn.BeginTransactionAsync();

        var saga = await _theSchema.LoadAsync(Guid.NewGuid(), tx, CancellationToken.None);
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task get_an_invalid_operation_exception_for_missing_id()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var db = await conn.BeginTransactionAsync();

        var saga = new LightweightSaga
        {
            Id = Guid.Empty,
            Name = "Xavier Worthy",
        };

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _theSchema.InsertAsync(saga, db, CancellationToken.None);
        });
    }

    [Fact]
    public async Task insert_then_load()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var db = await conn.BeginTransactionAsync();

        var saga = new LightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await _theSchema.InsertAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        using var db2 = await conn.BeginTransactionAsync();
        var saga2 = await _theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);

        saga2.Name.ShouldBe("Xavier Worthy");
    }

    [Fact]
    public async Task insert_update_then_load()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var db = await conn.BeginTransactionAsync();

        var saga = new LightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await _theSchema.InsertAsync(saga, db, CancellationToken.None);

        saga.Name = "Hollywood Brown";
        await _theSchema.UpdateAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        using var db2 = await conn.BeginTransactionAsync();
        var saga2 = await _theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);

        saga2.Name.ShouldBe("Hollywood Brown");
    }

    [Fact]
    public async Task insert_then_delete()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var db = await conn.BeginTransactionAsync();

        var saga = new LightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await _theSchema.InsertAsync(saga, db, CancellationToken.None);

        await _theSchema.DeleteAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        using var db2 = await conn.BeginTransactionAsync();
        var saga2 = await _theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);
        saga2.ShouldBeNull();
    }

    [Fact]
    public async Task concurrency_exception_when_version_does_not_match()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var db = await conn.BeginTransactionAsync();

        var saga = new LightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await _theSchema.InsertAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();
        await db.DisposeAsync();

        db = await conn.BeginTransactionAsync();

        saga.Name = "Rashee Rice";
        await _theSchema.UpdateAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();
        await db.DisposeAsync();

        db = await conn.BeginTransactionAsync();

        // I'm rewinding the version to make it throw
        saga.Version = 1;

        await Should.ThrowAsync<Exception>(async () =>
        {
            await _theSchema.UpdateAsync(saga, db, CancellationToken.None);
            await db.CommitAsync();
            await db.DisposeAsync();
        });
    }
}

public class LightweightSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}
