using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.SqlServer;
using JasperFx.Resources;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RateLimiting;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Wolverine.SqlServer.RateLimiting;
using Wolverine.SqlServer.Schema;
using Xunit;

namespace SqlServerTests;

public class rate_limiting_storage : IAsyncLifetime
{
    private readonly string _schemaName = $"rate_limits_{Guid.NewGuid():N}";
    private IHost? _host;

    public async Task InitializeAsync()
    {
        await waitForSqlServerAsync();
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(_schemaName);
        await conn.CloseAsync();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, _schemaName)
                    .UseSqlServerRateLimiting();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private static async Task waitForSqlServerAsync()
    {
        const int maxAttempts = 15;
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
                await conn.OpenAsync();
                await conn.CloseAsync();
                return;
            }
            catch (SqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(delay);
            }
        }
    }

    [Fact]
    public async Task creates_rate_limit_table_on_startup()
    {
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT table_schema, table_name FROM information_schema.tables WHERE table_schema = @schema AND table_name = @name";
        cmd.Parameters.Add(new SqlParameter("@schema", _schemaName));
        cmd.Parameters.Add(new SqlParameter("@name", "wolverine_rate_limits"));

        var found = false;
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                found = true;
            }
        }

        await conn.CloseAsync();

        found.ShouldBeTrue();
    }

    [Fact]
    public async Task rate_limit_store_allows_then_denies_and_resets()
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString,
            SchemaName = _schemaName
        };

        var persistence = new SqlServerMessageStore(settings, new DurabilitySettings(),
            NullLogger<SqlServerMessageStore>.Instance, Array.Empty<SagaTableDefinition>());
        persistence.AddTable(new RateLimitTable(_schemaName, "wolverine_rate_limits"));
        await persistence.RebuildAsync();

        var store = new SqlServerRateLimitStore(settings, new SqlServerRateLimitOptions { SchemaName = _schemaName });
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var limit = new RateLimit(2, 1.Minutes());
        var bucket = RateLimitBucket.For(limit, now);

        (await store.TryAcquireAsync(new RateLimitStoreRequest("key", bucket, 1, now), CancellationToken.None))
            .Allowed.ShouldBeTrue();
        (await store.TryAcquireAsync(new RateLimitStoreRequest("key", bucket, 1, now), CancellationToken.None))
            .Allowed.ShouldBeTrue();
        (await store.TryAcquireAsync(new RateLimitStoreRequest("key", bucket, 1, now), CancellationToken.None))
            .Allowed.ShouldBeFalse();

        var later = now.AddMinutes(1).AddSeconds(1);
        var nextBucket = RateLimitBucket.For(limit, later);
        (await store.TryAcquireAsync(new RateLimitStoreRequest("key", nextBucket, 1, later), CancellationToken.None))
            .Allowed.ShouldBeTrue();
    }
}
