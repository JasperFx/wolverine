using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
/// GH-4044. Marten owning the message store through IntegrateWithWolverine() alongside
/// Wolverine-managed conjoined EF Core tenancy against the same Postgres database.
///
/// <para>Marten hands Wolverine an NpgsqlDataSource, never a connection string, and
/// NpgsqlDataSource.ConnectionString deliberately drops the password -- so the connection-string
/// form of the conjoined registration cannot work here no matter what the message store surfaces.
/// The DbDataSource overload carries the credentials through intact; the connection-string form is
/// pinned below to fail with an error that says so.</para>
/// </summary>
[Collection("multi-tenancy")]
public class marten_owned_store_with_conjoined_efcore_tenancy : IAsyncLifetime
{
    private const string MartenSchema = "gh4044_marten";

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"DROP SCHEMA IF EXISTS {MartenOwnedItemsDbContext.SchemaName} CASCADE; " +
            $"DROP SCHEMA IF EXISTS {MartenSchema} CASCADE;";
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Task<IHost> startHostAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<MartenOwnedItemHandler>();

                opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<MartenOwnedItemsDbContext>(
                    (builder, dataSource) => builder.UseNpgsql((NpgsqlDataSource)dataSource),
                    AutoCreate.CreateOrUpdate,
                    tenancy => tenancy.PartitionPerTenant());

                // Marten, not PersistMessagesWithPostgresql(), owns envelope storage here
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = MartenSchema;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();
                opts.PublishAllMessages().Locally();
            }).StartAsync();
    }

    [Fact]
    public async Task the_host_starts_and_the_conjoined_db_context_resolves_a_connection_string()
    {
        using var host = await startHostAsync();

        var builder = host.Services.GetRequiredService<IDbContextBuilder<MartenOwnedItemsDbContext>>();
        var context = await builder.BuildAsync(CancellationToken.None);

        // Marten's data source is the one thing the conjoined builder can read here, so prove it
        // arrived rather than settling for "the host did not throw"
        context.Database.GetConnectionString()
            .ShouldNotBeNullOrEmpty();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_connection_string_overload_explains_why_it_cannot_work_here()
    {
        // Red baseline for the fix: without the DbDataSource overload the only string available is
        // the data source's, which has no password, so this used to die on a SCRAM login failure a
        // long way from the cause
        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;
                    opts.Discovery.DisableConventionalDiscovery();

                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<StringConfiguredDbContext>(
                        (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                        AutoCreate.CreateOrUpdate);

                    opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = MartenSchema;
                        m.DisableNpgsqlLogging = true;
                    }).IntegrateWithWolverine();

                    opts.UseEntityFrameworkCoreTransactions();
                    opts.Services.AddResourceSetupOnStartup();
                }).StartAsync();
        });

        ex.ToString().ShouldContain("AddDbContextWithWolverineManagedConjoinedTenancy overload that takes a DbDataSource");
    }

    [Fact]
    public async Task tenanted_writes_round_trip_through_the_conjoined_db_context()
    {
        using var host = await startHostAsync();

        var partitions = host.Services.GetRequiredService<IConjoinedTenantPartitions<MartenOwnedItemsDbContext>>();
        await partitions.AddTenantAsync("red", TestContext.Current.CancellationToken);
        await partitions.AddTenantAsync("blue", TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();
        await host.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("red", new CreateMartenOwnedItem(id, "red one")));

        var builder = host.Services.GetRequiredService<IDbContextBuilder<MartenOwnedItemsDbContext>>();
        var red = await builder.BuildAsync("red", CancellationToken.None);
        (await EntityFrameworkQueryableExtensions.SingleAsync(red.Items, x => x.Id == id, TestContext.Current.CancellationToken)).TenantId.ShouldBe("red");

        // ...and the tenant filter still holds, so this is conjoined tenancy and not just a DbContext
        var blue = await builder.BuildAsync("blue", CancellationToken.None);
        (await EntityFrameworkQueryableExtensions.AnyAsync(blue.Items, x => x.Id == id, TestContext.Current.CancellationToken)).ShouldBeFalse();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
