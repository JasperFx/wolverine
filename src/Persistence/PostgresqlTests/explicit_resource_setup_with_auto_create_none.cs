using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests;

public class explicit_resource_setup_with_auto_create_none : PostgresqlContext, IAsyncLifetime
{
    private const string SchemaName = "autocreate_none";

    private const string AncillarySchemaName = "autocreate_none_ancillary";

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);
        await conn.DropSchemaAsync(AncillarySchemaName);
        await conn.CloseAsync();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static IHostBuilder configureHost(
        ResourceMigrationFailureMode failureMode = ResourceMigrationFailureMode.FailFast)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.ResourceMigrationFailureMode = failureMode;
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName)
                    .OverrideAutoCreateResources(AutoCreate.None);
            });
    }

    private static IHostBuilder configureHostWithAncillaryStore()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName)
                    .OverrideAutoCreateResources(AutoCreate.None);
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, AncillarySchemaName,
                        MessageStoreRole.Ancillary)
                    .OverrideAutoCreateResources(AutoCreate.None);
            });
    }

    private static async Task dropAsync(string tableName)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand($"drop table {SchemaName}.{tableName}").ExecuteNonQueryAsync();
        await conn.CloseAsync();
    }

    /// <summary>
    ///     Schema drift that leaves every table in place: an extra column Wolverine never built. Weasel's
    ///     diff reports this as a difference; the cheap startup probe deliberately does not care.
    /// </summary>
    private static async Task addUnexpectedColumnAsync(string tableName)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand($"alter table {SchemaName}.{tableName} add column gh4166_drift varchar(25) null")
            .ExecuteNonQueryAsync();
        await conn.CloseAsync();
    }

    private static async Task<bool> envelopeTablesExist()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var tables = await conn.ExistingTablesAsync(schemas: [SchemaName]);
        await conn.CloseAsync();

        return tables.Any(x => x.Name == DatabaseConstants.IncomingTable);
    }

    [Fact]
    public async Task setup_resources_builds_the_message_storage_even_when_auto_create_is_none()
    {
        using var host = configureHost().Build();

        await host.SetupResources(cancellation: TestContext.Current.CancellationToken);

        (await envelopeTablesExist()).ShouldBeTrue();
    }

    [Fact]
    public async Task passive_migration_still_honors_auto_create_none()
    {
        using var host = configureHost().Build();

        var store = host.Services.GetRequiredService<IMessageStore>();
        await store.Admin.MigrateAsync();

        (await envelopeTablesExist()).ShouldBeFalse();
    }

    [Fact]
    public async Task migration_with_an_auto_create_override_builds_the_message_storage()
    {
        using var host = configureHost().Build();

        var store = host.Services.GetRequiredService<IMessageStore>();
        await store.Admin.MigrateAsync(AutoCreate.CreateOrUpdate);

        (await envelopeTablesExist()).ShouldBeTrue();
    }

    [Fact]
    public async Task host_startup_with_auto_build_none_does_not_create_the_message_storage()
    {
        using var host = configureHost().Build();

        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // Startup is expected to fail here - see the test below. This one only cares that it did
            // not create the storage on its way past.
        }

        (await envelopeTablesExist()).ShouldBeFalse();
    }

    /// <summary>
    ///     AutoCreate.None is a claim that something else provisioned the storage, so startup verifies the
    ///     claim instead of carrying on. Before this it logged "skipping ... must have been provisioned ahead
    ///     of time" and continued, and the first agent to touch a missing table then failed with a bare
    ///     "relation wolverine_nodes does not exist" from somewhere much further into startup - which names
    ///     neither the storage nor the setup step that was skipped.
    /// </summary>
    [Fact]
    public async Task host_startup_with_auto_build_none_fails_naming_the_setup_it_skipped()
    {
        using var host = configureHost().Build();

        var exception = await Should.ThrowAsync<Exception>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        // ToString rather than Message: the assert can surface wrapped, and what matters is that the
        // provisioning step is named somewhere in the failure the operator actually sees.
        exception.ToString().ShouldContain("resources setup");
    }

    /// <summary>
    ///     The check has to cover the same stores <c>MigrateAsync</c> does. An ancillary store whose schema
    ///     was never provisioned would otherwise pass startup unnoticed and fail whenever something first
    ///     used it, which can be a long way from here.
    /// </summary>
    [Fact]
    public async Task an_unprovisioned_ancillary_store_is_caught_as_well()
    {
        // Only the main store, so the ancillary schema is genuinely absent afterwards.
        using (var mainOnly = configureHost().Build())
        {
            await mainOnly.SetupResources(cancellation: TestContext.Current.CancellationToken);
        }

        using var host = configureHostWithAncillaryStore().Build();

        var exception = await Should.ThrowAsync<Exception>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        exception.ToString().ShouldContain("resources setup");
    }

    /// <summary>
    ///     And the other way round, so the test above cannot pass because setup never reached the ancillary
    ///     store in the first place.
    /// </summary>
    [Fact]
    public async Task setup_resources_provisions_the_ancillary_store_too()
    {
        using var host = configureHostWithAncillaryStore().Build();

        await host.SetupResources(cancellation: TestContext.Current.CancellationToken);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The GH-3130 compatibility path. A host whose code never touches the missing object used to start
    ///     perfectly well, so a deployment that provisions after its replicas has to keep that behaviour.
    /// </summary>
    [Fact]
    public async Task continue_on_failures_starts_the_host_when_a_table_is_missing()
    {
        using (var provisioned = configureHost().Build())
        {
            await provisioned.SetupResources(cancellation: TestContext.Current.CancellationToken);
        }

        // Nothing in the startup path reads the dead letter table, so this is a gap the host can tolerate -
        // and the check still has to see it, or the test proves nothing.
        await dropAsync(DatabaseConstants.DeadLetterTable);

        using var host = configureHost(ResourceMigrationFailureMode.ContinueOnFailures).Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task fail_fast_stops_the_host_when_a_table_is_missing()
    {
        using (var provisioned = configureHost().Build())
        {
            await provisioned.SetupResources(cancellation: TestContext.Current.CancellationToken);
        }

        await dropAsync(DatabaseConstants.DeadLetterTable);

        using var host = configureHost().Build();

        var exception = await Should.ThrowAsync<Exception>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        exception.ToString().ShouldContain("resources setup");
    }

    /// <summary>
    ///     GH-4166. AutoCreate.None is a claim that something ELSE owns this schema, so startup must not
    ///     fail over a schema that merely differs from what this Wolverine version would have built. 6.30.0
    ///     ran the full Weasel diff here and threw on any difference, which broke controlled-migration and
    ///     rolling-deploy setups that had been starting fine. Every table is present here; only its shape
    ///     differs, and that is the migration path's business, not startup's.
    /// </summary>
    [Fact]
    public async Task drift_that_leaves_the_tables_in_place_does_not_stop_startup()
    {
        using (var provisioned = configureHost().Build())
        {
            await provisioned.SetupResources(cancellation: TestContext.Current.CancellationToken);
        }

        await addUnexpectedColumnAsync(DatabaseConstants.IncomingTable);

        // Fail-fast mode deliberately, so this cannot pass merely because failures are being swallowed
        using var host = configureHost().Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task host_startup_with_auto_build_none_succeeds_once_the_storage_is_provisioned()
    {
        using var host = configureHost().Build();

        await host.SetupResources(cancellation: TestContext.Current.CancellationToken);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
