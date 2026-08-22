using IntegrationTests;
using Npgsql;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.Postgresql.Tests;

/// <summary>
/// GH-3509: <see cref="IClaimCheckStoreWithExpiration"/> support on the PostgreSQL backend.
/// </summary>
public class PostgresqlClaimCheckStoreExpirationTests : IAsyncLifetime
{
    private readonly string _schema = "claim_check_ttl_" + Guid.NewGuid().ToString("N")[..12];
    private NpgsqlDataSource _dataSource = null!;
    private PostgresqlClaimCheckStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        _store = new PostgresqlClaimCheckStore(_dataSource, _schema);

        await _store.DeleteAsync(new ClaimCheckToken("warmup", "text/plain", 0));
    }

    public async ValueTask DisposeAsync()
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

    private async Task<ClaimCheckToken> storeAged(TimeSpan age)
    {
        var token = await _store.StoreAsync(new byte[] { 1, 2, 3 }, "application/octet-stream",
            TestContext.Current.CancellationToken);

        await using var conn = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"update \"{_schema}\".\"wolverine_claim_check\" set created = @created where id = @id";
        cmd.Parameters.AddWithValue("created", DateTime.UtcNow - age);
        cmd.Parameters.AddWithValue("id", token.Id);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return token;
    }

    private async Task<long> countRows()
    {
        await using var conn = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from \"{_schema}\".\"wolverine_claim_check\"";
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task deletes_aged_rows_and_leaves_recent_ones()
    {
        var old = await storeAged(TimeSpan.FromHours(2));
        var recent = await storeAged(TimeSpan.FromMinutes(1));

        var deleted = await _store.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow.AddHours(-1), 100,
            TestContext.Current.CancellationToken);

        deleted.ShouldBe(1);

        await Should.ThrowAsync<KeyNotFoundException>(() => _store.LoadAsync(old));

        (await _store.LoadAsync(recent, TestContext.Current.CancellationToken)).ToArray()
            .ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task honors_the_max_count()
    {
        for (var i = 0; i < 5; i++)
        {
            await storeAged(TimeSpan.FromHours(2));
        }

        var deleted = await _store.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow.AddHours(-1), 2,
            TestContext.Current.CancellationToken);

        deleted.ShouldBe(2);
        (await countRows()).ShouldBe(3);
    }

    [Fact]
    public async Task a_repeat_sweep_is_a_no_op()
    {
        await storeAged(TimeSpan.FromHours(2));

        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        (await _store.DeleteExpiredPayloadsAsync(cutoff, 100, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await _store.DeleteExpiredPayloadsAsync(cutoff, 100, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task created_is_written_as_true_utc_regardless_of_the_session_time_zone()
    {
        // The column default was `now() at time zone 'utc'`, which lands local wall clock in a
        // timestamptz column. The store writes `created` explicitly so the sweep's UTC cutoff is
        // comparable no matter what time zone the session is running in.
        await using (var conn = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await using var setTz = conn.CreateCommand();
            setTz.CommandText = "set time zone 'America/Chicago'";
            await setTz.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var token = await _store.StoreAsync(new byte[] { 9 }, "text/plain", TestContext.Current.CancellationToken);

        await using var check = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = check.CreateCommand();
        cmd.CommandText = $"select created from \"{_schema}\".\"wolverine_claim_check\" where id = @id";
        cmd.Parameters.AddWithValue("id", token.Id);

        var created = (DateTime)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        // Within a couple of minutes of "now" -- an hours-off value means the offset bug is back.
        (DateTime.UtcNow - created).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task the_created_index_is_provisioned()
    {
        await using var conn = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "select count(*) from pg_indexes where schemaname = @schema and indexname = 'wolverine_claim_check_created_idx'";
        cmd.Parameters.AddWithValue("schema", _schema);

        ((long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(1);
    }
}
