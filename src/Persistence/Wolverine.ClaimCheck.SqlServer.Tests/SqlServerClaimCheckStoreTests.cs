using System.Text;
using IntegrationTests;
using Microsoft.Data.SqlClient;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.SqlServer.Tests;

public class SqlServerClaimCheckStoreTests : IAsyncLifetime
{
    // Each test class gets its own schema so parallel classes / re-runs never collide,
    // mirroring the PostgreSQL backend.
    private readonly string _schema = "claim_check_" + Guid.NewGuid().ToString("N")[..12];
    private SqlServerClaimCheckStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new SqlServerClaimCheckStore(Servers.SqlServerConnectionString, _schema);

        // touch the store so the schema/table is provisioned before assertions
        await _store.DeleteAsync(new ClaimCheckToken("warmup", "text/plain", 0));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"drop table if exists [{_schema}].[wolverine_claim_check]; drop schema if exists [{_schema}];";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task round_trip_store_load_delete()
    {
        var payload = Encoding.UTF8.GetBytes("hello, claim check world");

        var token = await _store.StoreAsync(payload, "text/plain", TestContext.Current.CancellationToken);

        token.Id.ShouldNotBeNullOrWhiteSpace();
        token.ContentType.ShouldBe("text/plain");
        token.Length.ShouldBe(payload.Length);

        var loaded = await _store.LoadAsync(token, TestContext.Current.CancellationToken);
        loaded.ToArray().ShouldBe(payload);

        await _store.DeleteAsync(token, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<KeyNotFoundException>(async () => await _store.LoadAsync(token));
    }

    [Fact]
    public async Task delete_is_idempotent_for_missing_row()
    {
        var token = new ClaimCheckToken("does_not_exist_" + Guid.NewGuid().ToString("N"), "text/plain", 0);

        await _store.DeleteAsync(token, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task load_returns_exact_payload_bytes()
    {
        // Binary payload with zero bytes and high bits set, to catch any encoding-related
        // corruption in the varbinary(max) round trip.
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        var token = await _store.StoreAsync(payload, "application/octet-stream",
            TestContext.Current.CancellationToken);

        (await _store.LoadAsync(token, TestContext.Current.CancellationToken)).ToArray().ShouldBe(payload);
    }

    [Fact]
    public async Task handles_a_payload_larger_than_the_8000_byte_inline_limit()
    {
        // varbinary(max) is the whole point of this backend -- anything over 8000 bytes goes off-row,
        // which is exactly the size range claim checks exist to carry.
        var payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);

        var token = await _store.StoreAsync(payload, "application/octet-stream",
            TestContext.Current.CancellationToken);

        token.Length.ShouldBe(payload.Length);
        (await _store.LoadAsync(token, TestContext.Current.CancellationToken)).ToArray().ShouldBe(payload);
    }

    [Fact]
    public void rejects_a_multi_part_schema_name()
    {
        // GH-3997: SQL Server would read "crm.sales" as a multi-part name and the DDL would not mean
        // what the caller intended.
        var ex = Should.Throw<ArgumentException>(() =>
            new SqlServerClaimCheckStore(Servers.SqlServerConnectionString, "crm.sales"));

        ex.Message.ShouldContain("multi-part");
    }

    [Fact]
    public void rejects_an_identifier_that_could_escape_its_brackets()
    {
        Should.Throw<ArgumentException>(() =>
            new SqlServerClaimCheckStore(Servers.SqlServerConnectionString, "dbo", "x] ; drop table y --"));
    }
}
