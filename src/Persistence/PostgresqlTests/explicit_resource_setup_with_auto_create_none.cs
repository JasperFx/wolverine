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

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);
        await conn.CloseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    private static IHostBuilder configureHost()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName)
                    .OverrideAutoCreateResources(AutoCreate.None);
            });
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

        await host.SetupResources();

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
            await host.StartAsync();
            await host.StopAsync();
        }
        catch (Exception)
        {
            // The host may well fail during startup because the message storage was
            // never provisioned. This test only cares that startup did not create it
        }

        (await envelopeTablesExist()).ShouldBeFalse();
    }
}
