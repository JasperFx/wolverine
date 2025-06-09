using IntegrationTests;
using Microsoft.Data.SqlClient;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.SqlServer.Sagas;

namespace SqlServerTests.Sagas;

public class saga_storage_operations : SqlServerContext
{
    private readonly DatabaseSagaSchema<Guid, LightweightSaga> _theSchema;

    public saga_storage_operations()
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString,
            SchemaName = "lightweight_sagas",
        };

        var definition = new SagaTableDefinition(typeof(LightweightSaga), null);
        _theSchema = new DatabaseSagaSchema<Guid, LightweightSaga>(definition, settings);
    }

    [Fact]
    public async Task load_with_no_document_happily_returns_null()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        using var tx = await conn.BeginTransactionAsync();
        
        var saga = await _theSchema.LoadAsync(Guid.NewGuid(), tx, CancellationToken.None);
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task get_an_argument_out_of_range_exception_for_missing_id()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var db = await conn.BeginTransactionAsync();

        var saga = new LightweightSaga
        {
            Id = Guid.Empty,
            Name = "Xavier Worthy",
        };

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await _theSchema.InsertAsync(saga, db, CancellationToken.None);
        });
    }

    [Fact]
    public async Task insert_then_load()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
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
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
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
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
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
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        
        await conn.CreateCommand("delete from lightweight_sagas.lightweightsaga_saga")
            .ExecuteNonQueryAsync();
        
        await using var db = await conn.BeginTransactionAsync();

        var saga = new LightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };
        
        await _theSchema.InsertAsync(saga, db, CancellationToken.None);

        saga.Name = "Rashee Rice";
        await _theSchema.UpdateAsync(saga, db, CancellationToken.None);

        // I'm rewinding the version to make it throw
        saga.Version = 1;

        await Should.ThrowAsync<SagaConcurrencyException>(async () =>
        {
            await _theSchema.UpdateAsync(saga, db, CancellationToken.None);
        });
    }
}

public class LightweightSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}