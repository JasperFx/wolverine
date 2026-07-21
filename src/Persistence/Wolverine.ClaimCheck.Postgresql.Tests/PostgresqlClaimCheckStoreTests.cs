using System.Text;
using IntegrationTests;
using Npgsql;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.Postgresql.Tests;

public class PostgresqlClaimCheckStoreTests : IAsyncLifetime
{
    // Each test class gets its own schema so parallel classes / re-runs never collide,
    // mirroring the Amazon S3 backend's per-class bucket.
    private readonly string _schema = "claim_check_" + Guid.NewGuid().ToString("N")[..12];
    private NpgsqlDataSource _dataSource = null!;
    private PostgresqlClaimCheckStore _store = null!;

    public async Task InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        _store = new PostgresqlClaimCheckStore(_dataSource, _schema);

        // touch the store so the schema/table is provisioned before assertions
        await _store.DeleteAsync(new ClaimCheckToken("warmup", "text/plain", 0));
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"drop schema if exists \"{_schema}\" cascade";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // best-effort cleanup
        }
        finally
        {
            await _dataSource.DisposeAsync();
        }
    }

    [Fact]
    public async Task round_trip_store_load_delete()
    {
        var payload = Encoding.UTF8.GetBytes("hello, claim check world");

        var token = await _store.StoreAsync(payload, "text/plain");

        token.Id.ShouldNotBeNullOrWhiteSpace();
        token.ContentType.ShouldBe("text/plain");
        token.Length.ShouldBe(payload.Length);

        var loaded = await _store.LoadAsync(token);
        loaded.ToArray().ShouldBe(payload);

        await _store.DeleteAsync(token);

        // After delete, loading should fail with a not-found error.
        await Should.ThrowAsync<KeyNotFoundException>(async () => await _store.LoadAsync(token));
    }

    [Fact]
    public async Task delete_is_idempotent_for_missing_row()
    {
        var token = new ClaimCheckToken("does_not_exist_" + Guid.NewGuid().ToString("N"), "text/plain", 0);

        // Should not throw even though the row was never created.
        await _store.DeleteAsync(token);
    }

    [Fact]
    public async Task load_returns_exact_payload_bytes()
    {
        // Binary payload with zero bytes and high bits set to catch any encoding-related
        // corruption in the bytea round-trip.
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        var token = await _store.StoreAsync(payload, "application/octet-stream");
        var loaded = await _store.LoadAsync(token);

        loaded.Length.ShouldBe(payload.Length);
        loaded.ToArray().ShouldBe(payload);
    }

    [Fact]
    public async Task provisioning_is_idempotent_across_stores()
    {
        // A second store over the same schema/table must not fail re-running the
        // create-if-not-exists DDL, and must see the first store's row.
        var token = await _store.StoreAsync(Encoding.UTF8.GetBytes("shared"), "text/plain");

        var second = new PostgresqlClaimCheckStore(_dataSource, _schema);
        var loaded = await second.LoadAsync(token);

        loaded.ToArray().ShouldBe(Encoding.UTF8.GetBytes("shared"));
    }

    [Fact]
    public void rejects_non_identifier_schema_names()
    {
        Should.Throw<ArgumentException>(() =>
            new PostgresqlClaimCheckStore(_dataSource, "bad name; drop table x"));
    }
}
