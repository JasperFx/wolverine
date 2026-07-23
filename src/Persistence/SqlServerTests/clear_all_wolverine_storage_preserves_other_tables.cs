using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.RateLimiting;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace SqlServerTests;

/// <summary>
/// The negative control behind the GH-3592 reversal. <c>AddTable</c> is a general registration path
/// on the message store, and SQL Server's rate-limit table rides on it — so a reset that blanket-
/// truncated everything registered there would silently reopen every rate-limited endpoint. That
/// ambiguity is why reset stayed envelope-storage-only and the full wipe became explicit.
/// <c>ClearAllWolverineStorageAsync()</c> clears envelope storage and database queue tables, and
/// nothing else.
/// </summary>
[Collection("sqlserver")]
public class clear_all_wolverine_storage_preserves_other_tables : IAsyncLifetime
{
    private const string SchemaName = "clear_all_other_tables";

    private IHost theHost = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
            await conn.CloseAsync();
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName)
                    .UseSqlServerRateLimiting();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task the_rate_limit_table_survives_the_reset()
    {
        var store = theHost.Services.GetRequiredService<IRateLimitStore>();

        var bucket = RateLimitBucket.For(RateLimit.PerHour(5), DateTimeOffset.UtcNow);
        var result = await store.TryAcquireAsync(
            new RateLimitStoreRequest("clear-all-storage", bucket, 1, DateTimeOffset.UtcNow),
            CancellationToken.None);

        result.Allowed.ShouldBeTrue();
        (await rateLimitRowCountAsync()).ShouldBeGreaterThan(0);

        await theHost.ClearAllWolverineStorageAsync();

        (await rateLimitRowCountAsync()).ShouldBeGreaterThan(0);
    }

    private static async Task<int> rateLimitRowCountAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"select count(*) from [{SchemaName}].[wolverine_rate_limits]";
            return (int)(await cmd.ExecuteScalarAsync())!;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
