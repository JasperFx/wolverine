using IntegrationTests;
using Microsoft.Data.SqlClient;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.SqlServer.Tests;

/// <summary>
/// GH-3509 / GH-3566: <see cref="IClaimCheckStoreWithExpiration"/> support on the SQL Server backend.
/// Mirrors the PostgreSQL expiration suite.
/// </summary>
public class SqlServerClaimCheckStoreExpirationTests : IAsyncLifetime
{
    private readonly string _schema = "claim_check_ttl_" + Guid.NewGuid().ToString("N")[..12];
    private SqlServerClaimCheckStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new SqlServerClaimCheckStore(Servers.SqlServerConnectionString, _schema);
        await _store.DeleteAsync(new ClaimCheckToken("warmup", "text/plain", 0));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var conn = await openAsync(CancellationToken.None);
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

    private static async Task<SqlConnection> openAsync(CancellationToken token)
    {
        var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(token);
        return conn;
    }

    private async Task<ClaimCheckToken> storeAged(TimeSpan age)
    {
        var token = await _store.StoreAsync(new byte[] { 1, 2, 3 }, "application/octet-stream",
            TestContext.Current.CancellationToken);

        await using var conn = await openAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"update [{_schema}].[wolverine_claim_check] set created = @created where id = @id";
        cmd.Parameters.AddWithValue("@created", DateTime.UtcNow - age);
        cmd.Parameters.AddWithValue("@id", token.Id);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return token;
    }

    private async Task<int> countRows()
    {
        await using var conn = await openAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from [{_schema}].[wolverine_claim_check]";
        return (int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
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
    public async Task a_non_positive_max_count_deletes_nothing()
    {
        await storeAged(TimeSpan.FromHours(2));

        (await _store.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow.AddHours(-1), 0,
            TestContext.Current.CancellationToken)).ShouldBe(0);

        (await countRows()).ShouldBe(1);
    }

    [Fact]
    public async Task created_is_written_as_utc()
    {
        var token = await _store.StoreAsync(new byte[] { 9 }, "text/plain", TestContext.Current.CancellationToken);

        await using var conn = await openAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select created from [{_schema}].[wolverine_claim_check] where id = @id";
        cmd.Parameters.AddWithValue("@id", token.Id);

        var created = (DateTime)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        // An hours-off value would mean the store wrote local time, which the UTC sweep cutoff
        // could never match. See the same regression on the PostgreSQL store.
        (DateTime.UtcNow - created).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task the_created_index_is_provisioned()
    {
        await using var conn = await openAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from sys.indexes where name = 'wolverine_claim_check_created_idx' " +
            $"and object_id = object_id('{_schema}.wolverine_claim_check')";

        ((int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(1);
    }
}
