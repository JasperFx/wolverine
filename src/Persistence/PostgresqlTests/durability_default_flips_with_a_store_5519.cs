using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests;

/// <summary>
/// GH-5519. The 7.0 durability defaults in an application with a message store. Scheduled local messages are covered
/// by scheduled_messages_use_message_store_when_AlwaysMakeScheduledMessagesDurable_is_set and the inbox partitioning
/// migration by inbox_partitioning_migration_5519; the store-less side is CoreTests' durability_default_flips_5519.
/// </summary>
[Collection("marten")]
public class durability_default_flips_with_a_store_5519
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task the_7_0_defaults_with_a_postgresql_store()
    {
        const string schema = "flips_5519_default";
        using var host = await startAsync(schema, _ => { });

        var durability = host.Services.GetRequiredService<WolverineOptions>().Durability;
        durability.DeadLetterQueueExpirationEnabled.ShouldBeTrue();
        durability.InboxStaleTime.ShouldBe(1.Hours());
        durability.OutboxStaleTime.ShouldBe(1.Hours());
        durability.EnableInboxPartitioning.ShouldBeTrue();

        (await columnExistsAsync(schema, DatabaseConstants.DeadLetterTable, DatabaseConstants.Expires)).ShouldBeTrue();
        (await isPartitionedAsync(schema)).ShouldBeTrue();
    }

    [Fact]
    public async Task the_6x_values_after_restoring_the_v6_defaults()
    {
        const string schema = "flips_5519_v6";
        using var host = await startAsync(schema, opts => opts.RestoreV6Defaults());

        var durability = host.Services.GetRequiredService<WolverineOptions>().Durability;
        durability.DeadLetterQueueExpirationEnabled.ShouldBeFalse();
        durability.InboxStaleTime.ShouldBeNull();
        durability.OutboxStaleTime.ShouldBeNull();
        durability.EnableInboxPartitioning.ShouldBeFalse();

        (await columnExistsAsync(schema, DatabaseConstants.DeadLetterTable, DatabaseConstants.Expires)).ShouldBeFalse();
        (await isPartitionedAsync(schema)).ShouldBeFalse();
    }

    [Fact]
    public async Task explicit_settings_win_over_the_7_0_defaults()
    {
        const string schema = "flips_5519_explicit";
        using var host = await startAsync(schema, opts =>
        {
            opts.Durability.DeadLetterQueueExpirationEnabled = false;
            opts.Durability.EnableInboxPartitioning = false;
            opts.Durability.InboxStaleTime = null;
            opts.Durability.OutboxStaleTime = 10.Minutes();
        });

        var durability = host.Services.GetRequiredService<WolverineOptions>().Durability;
        durability.InboxStaleTime.ShouldBeNull();
        durability.OutboxStaleTime.ShouldBe(10.Minutes());

        (await columnExistsAsync(schema, DatabaseConstants.DeadLetterTable, DatabaseConstants.Expires)).ShouldBeFalse();
        (await isPartitionedAsync(schema)).ShouldBeFalse();
    }

    private static async Task<IHost> startAsync(string schema, Action<WolverineOptions> configure)
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(ct);
            await conn.CreateCommand($"drop schema if exists {schema} cascade").ExecuteNonQueryAsync(ct);
        }

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                configure(opts);
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(ct);
    }

    private static async Task<bool> columnExistsAsync(string schema, string table, string column)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand(
            "select count(*) from information_schema.columns where table_schema = :schema and table_name = :table and column_name = :column");
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);
        cmd.Parameters.AddWithValue("column", column);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private static async Task<bool> isPartitionedAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand(
            "select count(*) from pg_partitioned_table p join pg_class c on c.oid = p.partrelid " +
            "join pg_namespace n on n.oid = c.relnamespace where n.nspname = :schema and c.relname = :table");
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", DatabaseConstants.IncomingTable);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }
}
