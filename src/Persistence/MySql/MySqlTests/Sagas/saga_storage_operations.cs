using IntegrationTests;
using MySqlConnector;
using Shouldly;
using Weasel.MySql;
using Wolverine;
using Wolverine.MySql.Internals;
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
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var saga = await theSchema.LoadAsync(Guid.NewGuid(), tx, CancellationToken.None);
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task get_an_argument_out_of_range_exception_for_missing_id()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

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
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var saga = new MySqlLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);
        await db.CommitAsync(TestContext.Current.CancellationToken);

        using var db2 = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var saga2 = await theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);

        saga2.ShouldNotBeNull();
        saga2.Name.ShouldBe("Xavier Worthy");
    }

    [Fact]
    public async Task insert_update_then_load()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var saga = new MySqlLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);

        saga.Name = "Hollywood Brown";
        await theSchema.UpdateAsync(saga, db, CancellationToken.None);
        await db.CommitAsync(TestContext.Current.CancellationToken);

        using var db2 = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var saga2 = await theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);

        saga2.ShouldNotBeNull();
        saga2.Name.ShouldBe("Hollywood Brown");
    }

    [Fact]
    public async Task insert_then_delete()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var saga = new MySqlLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);

        await theSchema.DeleteAsync(saga, db, CancellationToken.None);
        await db.CommitAsync(TestContext.Current.CancellationToken);

        using var db2 = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var saga2 = await theSchema.LoadAsync(saga.Id, db2, CancellationToken.None);
        saga2.ShouldBeNull();
    }

    [Fact]
    public async Task concurrency_exception_when_version_does_not_match()
    {
        await theSchema.EnsureStorageExistsAsync(CancellationToken.None);

        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        // Clean up the table
        await using var cleanCmd = conn.CreateCommand();
        cleanCmd.CommandText = "DELETE FROM lightweight_sagas.mysqllightweightsaga_saga";
        await cleanCmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var saga = new MySqlLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
        };

        await theSchema.InsertAsync(saga, db, CancellationToken.None);
        await db.CommitAsync(TestContext.Current.CancellationToken);
        await db.DisposeAsync();

        db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        saga.Name = "Rashee Rice";
        await theSchema.UpdateAsync(saga, db, CancellationToken.None);
        await db.CommitAsync(TestContext.Current.CancellationToken);
        await db.DisposeAsync();

        db = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        // I'm rewinding the version to make it throw
        saga.Version = 1;

        await Should.ThrowAsync<SagaConcurrencyException>(async () =>
        {
            await theSchema.UpdateAsync(saga, db, CancellationToken.None);
            await db.CommitAsync();
            await db.DisposeAsync();
        });
    }
    // GH-5345. The column keeps whatever precision the database has, so values read back are compared to
    // what was written with a small tolerance; ordering is compared strictly between two values read back.
    [Fact]
    public async Task insert_and_update_stamp_last_modified_over_any_user_value()
    {
        await theSchema.EnsureStorageExistsAsync(CancellationToken.None);
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var backdated = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var saga = new MySqlLightweightSaga
        {
            Id = Guid.NewGuid(),
            Name = "Xavier Worthy",
            LastModified = backdated
        };

        var before = DateTimeOffset.UtcNow;
        await using (var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await theSchema.InsertAsync(saga, tx, CancellationToken.None);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }

        saga.LastModified.ShouldBeGreaterThanOrEqualTo(before);

        MySqlLightweightSaga inserted;
        await using (var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            inserted = (await theSchema.LoadAsync(saga.Id, tx, CancellationToken.None))!;
        }

        (inserted.LastModified - saga.LastModified).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));

        await Task.Delay(1100, TestContext.Current.CancellationToken);

        inserted.Name = "Hollywood Brown";
        inserted.LastModified = backdated;
        await using (var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await theSchema.UpdateAsync(inserted, tx, CancellationToken.None);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }

        MySqlLightweightSaga updated;
        await using (var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            updated = (await theSchema.LoadAsync(saga.Id, tx, CancellationToken.None))!;
        }

        updated.Name.ShouldBe("Hollywood Brown");
        updated.LastModified.ShouldBeGreaterThan(saga.LastModified);
        (updated.LastModified - inserted.LastModified).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task load_backfills_last_modified_from_the_column_for_a_row_written_before_7_0()
    {
        await theSchema.EnsureStorageExistsAsync(CancellationToken.None);
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        // A body with no LastModified at all, as every saga written before 7.0 has
        var id = Guid.NewGuid();
        var body = $"{{\"Id\":\"{id}\",\"Name\":\"Old Saga\"}}";

        var table = new SagaTableDefinition(typeof(MySqlLightweightSaga), null).TableName;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO lightweight_sagas.{table} (id, body, version) VALUES (@id, @body, 1)";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@body", body);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var loaded = await theSchema.LoadAsync(id, tx, CancellationToken.None);

        loaded!.Name.ShouldBe("Old Saga");
        loaded.LastModified.ShouldNotBe(default);
        (loaded.LastModified - DateTimeOffset.UtcNow).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(5));
    }

}

public class MySqlLightweightSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
