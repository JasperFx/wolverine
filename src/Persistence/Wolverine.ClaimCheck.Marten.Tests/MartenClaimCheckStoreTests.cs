using System.Text;
using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.Marten.Tests;

public class MartenClaimCheckStoreTests : IAsyncLifetime
{
    private readonly string _schema = "cc_marten_" + Guid.NewGuid().ToString("N")[..12];
    private IHost _host = null!;
    private IClaimCheckStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddMarten(opts =>
                {
                    opts.Connection(Servers.PostgresConnectionString);
                    opts.DatabaseSchemaName = _schema;
                });
            })
            .UseWolverine(opts => opts.UseClaimCheck(cc => cc.UseMartenClaimCheck()))
            .StartAsync();

        _store = _host.Services.GetRequiredService<IClaimCheckStore>();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var store = _host.Services.GetRequiredService<IDocumentStore>();
            await using var conn = MartenClaimCheckStore.DataSourceFor(store).CreateConnection();
            await conn.OpenAsync();
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
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public void resolves_a_marten_backed_store_rather_than_the_file_system_fallback()
    {
        // GH-3564's deferred-store path is what makes this work at all -- IDocumentStore does not exist
        // when UseClaimCheck runs, so a naive registration would silently fall back to the file system.
        _store.ShouldBeOfType<MartenClaimCheckStore>();
    }

    [Fact]
    public void uses_martens_own_schema_by_default()
    {
        _store.ShouldBeOfType<MartenClaimCheckStore>().SchemaName.ShouldBe(_schema);
    }

    [Fact]
    public async Task round_trip_store_load_delete()
    {
        var payload = Encoding.UTF8.GetBytes("hello from a marten-hosted claim check");

        var token = await _store.StoreAsync(payload, "text/plain", TestContext.Current.CancellationToken);

        token.ContentType.ShouldBe("text/plain");
        token.Length.ShouldBe(payload.Length);

        (await _store.LoadAsync(token, TestContext.Current.CancellationToken)).ToArray().ShouldBe(payload);

        await _store.DeleteAsync(token, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<KeyNotFoundException>(async () => await _store.LoadAsync(token));
    }

    [Fact]
    public async Task stores_raw_bytes_not_base64_json()
    {
        // The whole point of choosing a bytea table over a Marten document: the body must be stored at
        // its true length, not inflated ~33% by base64 inside JSONB.
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        var token = await _store.StoreAsync(payload, "application/octet-stream",
            TestContext.Current.CancellationToken);

        var documentStore = _host.Services.GetRequiredService<IDocumentStore>();
        await using var conn = MartenClaimCheckStore.DataSourceFor(documentStore).CreateConnection();
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select octet_length(body) from \"{_schema}\".\"wolverine_claim_check\" where id = @id";
        cmd.Parameters.AddWithValue("id", token.Id);

        var storedLength = (int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        storedLength.ShouldBe(payload.Length);
    }

    [Fact]
    public async Task supports_expiration_so_the_sweeper_can_reach_it()
    {
        // The sweeper unwraps the deferred proxy before testing for IClaimCheckStoreWithExpiration --
        // a type test against the proxy would report "not supported" and silently skip this backend.
        var expiring = _store.ShouldBeAssignableTo<IClaimCheckStoreWithExpiration>();

        var token = await _store.StoreAsync(new byte[] { 1, 2, 3 }, "text/plain",
            TestContext.Current.CancellationToken);

        var deleted = await expiring!.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow.AddHours(1), 100,
            TestContext.Current.CancellationToken);

        deleted.ShouldBeGreaterThanOrEqualTo(1);
        await Should.ThrowAsync<KeyNotFoundException>(async () => await _store.LoadAsync(token));
    }
}
