using IntegrationTests;
using Microsoft.Data.SqlClient;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.SqlServer.Internals;

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
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        
        var saga = await _theSchema.LoadAsync(Guid.NewGuid(), tx, CancellationToken.None);
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task get_an_argument_out_of_range_exception_for_missing_id()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

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
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var saga = new LightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };
        
        await _theSchema.InsertAsync(saga, db, CancellationToken.None);
        await db.CommitAsync(TestContext.Current.CancellationToken);
        
        using var db2 = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var saga2 = await _theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);
        
        saga2!.Name.ShouldBe("Xavier Worthy");
    }

    [Fact]
    public async Task insert_update_then_load()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var saga = new LightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };
        
        await _theSchema.InsertAsync(saga, db, CancellationToken.None);

        saga.Name = "Hollywood Brown";
        await _theSchema.UpdateAsync(saga, db, CancellationToken.None);
        await db.CommitAsync(TestContext.Current.CancellationToken);

        using var db2 = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var saga2 = await _theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);
        
        saga2!.Name.ShouldBe("Hollywood Brown");
    }

    [Fact]
    public async Task insert_then_delete()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var saga = new LightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };
        
        await _theSchema.InsertAsync(saga, db, CancellationToken.None);

        await _theSchema.DeleteAsync(saga, db, CancellationToken.None);
        await db.CommitAsync(TestContext.Current.CancellationToken);

        using var db2 = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var saga2 = await _theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);
        saga2.ShouldBeNull();
    }

    [Fact]
    public async Task concurrency_exception_when_version_does_not_match()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        // No raw table cleanup here: the saga uses a fresh Guid so residue cannot affect the
        // assertions, and a raw `delete from` was the one statement in this class that bypassed the
        // schema API's ensure-storage — on a fresh catalog (each worker lane under parallelized CI)
        // it failed with "Invalid object name" whenever this test ran before any sibling that
        // auto-created the table. Test-ordering luck, not a real dependency.
        await using var db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

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
    // GH-5345. The column keeps whatever precision the database has, so values read back are compared to
    // what was written with a small tolerance; ordering is compared strictly between two values read back.
    [Fact]
    public async Task insert_and_update_stamp_last_modified_over_any_user_value()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var backdated = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var saga = new LightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
            LastModified = backdated
        };

        var before = DateTimeOffset.UtcNow;
        await using (var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await _theSchema.InsertAsync(saga, tx, CancellationToken.None);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }

        saga.LastModified.ShouldBeGreaterThanOrEqualTo(before);

        LightweightSaga inserted;
        await using (var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            inserted = (await _theSchema.LoadAsync(saga.Id, tx, CancellationToken.None))!;
        }

        (inserted.LastModified - saga.LastModified).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));

        await Task.Delay(1100, TestContext.Current.CancellationToken);

        inserted.Name = "Hollywood Brown";
        inserted.LastModified = backdated;
        await using (var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await _theSchema.UpdateAsync(inserted, tx, CancellationToken.None);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }

        LightweightSaga updated;
        await using (var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            updated = (await _theSchema.LoadAsync(saga.Id, tx, CancellationToken.None))!;
        }

        updated.Name.ShouldBe("Hollywood Brown");
        updated.LastModified.ShouldBeGreaterThan(saga.LastModified);
        (updated.LastModified - inserted.LastModified).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task load_backfills_last_modified_from_the_column_for_a_row_written_before_7_0()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        // The schema creates its table on first use
        await using (var setup = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await _theSchema.InsertAsync(new LightweightSaga { Id = Guid.NewGuid(), Name = "Setup" }, setup,
                CancellationToken.None);
            await setup.CommitAsync(TestContext.Current.CancellationToken);
        }

        // A body with no LastModified at all, as every saga written before 7.0 has
        var id = Guid.NewGuid();
        var body = $"{{\"Id\":\"{id}\",\"Name\":\"Old Saga\"}}";

        var table = new SagaTableDefinition(typeof(LightweightSaga), null).TableName;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"insert into lightweight_sagas.{table} (id, body, version) values (@id, @body, 1)";
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("body", System.Text.Encoding.UTF8.GetBytes(body));
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var loaded = await _theSchema.LoadAsync(id, tx, CancellationToken.None);

        loaded!.Name.ShouldBe("Old Saga");
        loaded.LastModified.ShouldNotBe(default);
        (loaded.LastModified - DateTimeOffset.UtcNow).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(5));
    }

}

public class LightweightSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}