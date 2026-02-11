using IntegrationTests;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Oracle;
using Wolverine;
using Wolverine.Oracle.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace OracleTests.Sagas;

[Collection("oracle")]
public class saga_storage_operations
{
    private readonly OracleSagaSchema<OracleLightweightSaga, Guid> theSchema;

    public saga_storage_operations()
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.OracleConnectionString,
            SchemaName = "WOLVERINE",
        };

        var definition = new SagaTableDefinition(typeof(OracleLightweightSaga), null);
        theSchema = new OracleSagaSchema<OracleLightweightSaga, Guid>(definition, settings);
    }

    [Fact]
    public async Task load_with_no_document_happily_returns_null()
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();

        using var tx = (OracleTransaction)await conn.BeginTransactionAsync();

        var saga = await theSchema.LoadAsync(Guid.NewGuid(), tx, CancellationToken.None);
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task get_an_argument_out_of_range_exception_for_missing_id()
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();
        var db = (OracleTransaction)await conn.BeginTransactionAsync();

        var saga = new OracleLightweightSaga
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
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();
        var db = (OracleTransaction)await conn.BeginTransactionAsync();

        var saga = new OracleLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        var db2 = (OracleTransaction)await conn.BeginTransactionAsync();
        var saga2 = await theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);

        saga2.ShouldNotBeNull();
        saga2.Name.ShouldBe("Xavier Worthy");
    }

    [Fact]
    public async Task insert_update_then_load()
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();
        var db = (OracleTransaction)await conn.BeginTransactionAsync();

        var saga = new OracleLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);

        saga.Name = "Hollywood Brown";
        await theSchema.UpdateAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        var db2 = (OracleTransaction)await conn.BeginTransactionAsync();
        var saga2 = await theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);

        saga2.ShouldNotBeNull();
        saga2.Name.ShouldBe("Hollywood Brown");
    }

    [Fact]
    public async Task insert_then_delete()
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();
        var db = (OracleTransaction)await conn.BeginTransactionAsync();

        var saga = new OracleLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);

        await theSchema.DeleteAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        var db2 = (OracleTransaction)await conn.BeginTransactionAsync();
        var saga2 = await theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);
        saga2.ShouldBeNull();
    }

    [Fact]
    public async Task concurrency_exception_when_version_does_not_match()
    {
        await theSchema.EnsureStorageExistsAsync(CancellationToken.None);

        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();

        // Clean up the table
        var cleanCmd = conn.CreateCommand(
            $"DELETE FROM WOLVERINE.{nameof(OracleLightweightSaga).ToUpperInvariant()}_SAGA");
        await cleanCmd.ExecuteNonQueryAsync();

        var db = (OracleTransaction)await conn.BeginTransactionAsync();

        var saga = new OracleLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        db = (OracleTransaction)await conn.BeginTransactionAsync();

        saga.Name = "Rashee Rice";
        await theSchema.UpdateAsync(saga, db, CancellationToken.None);
        await db.CommitAsync();

        db = (OracleTransaction)await conn.BeginTransactionAsync();

        // I'm rewinding the version to make it throw
        saga.Version = 1;

        await Should.ThrowAsync<SagaConcurrencyException>(async () =>
        {
            await theSchema.UpdateAsync(saga, db, CancellationToken.None);
            await db.CommitAsync();
        });
    }
}

public class OracleLightweightSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
