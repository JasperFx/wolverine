using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http.Tests.EfCoreOnly;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Http.Tests.MultiTenancy;

/// <summary>
/// An EF Core application with Wolverine-managed multi-tenancy that also calls
/// <c>UseEntityFrameworkCoreTransactions()</c>, the configuration the reported defects met in:
///
/// <list type="bullet">
/// <item>
/// <c>[Entity]</c> on a step method's parameter (<c>Validate</c>, <c>Before</c>, <c>Load</c>) did not make the
/// chain transactional, so a change to the loaded entity was silently never saved. Only the endpoint
/// method's own parameters were scanned for load attributes.
/// </item>
/// <item>
/// The same step <c>[Entity]</c> did not even compile against a route id: EF Core's load frame never declared
/// its dependency on the identity, so the frame parsing the route value was ordered after the load.
/// </item>
/// </list>
///
/// Every row is seeded in the tenant database only, so reading through the main database's DbContext shows up
/// as a 404 or a missing row rather than passing by accident.
/// </summary>
public class entity_on_step_methods_with_managed_multi_tenancy : IAsyncLifetime
{
    private const string TenantDatabase = "step_entity_red";

    private static readonly Type[] EndpointTypes =
    [
        typeof(StepEntityRenameEndpoint),
        typeof(StepEntityRenameOnEndpoint)
    ];

    private IAlbaHost theHost = null!;
    private string theTenantConnectionString = null!;

    public async ValueTask InitializeAsync()
    {
        theTenantConnectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = TenantDatabase
        }.ConnectionString;

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var exists = new NpgsqlCommand($"select 1 from pg_database where datname = '{TenantDatabase}'", conn);
            if (await exists.ExecuteScalarAsync() == null)
            {
                await using var create = new NpgsqlCommand($"create database {TenantDatabase}", conn);
                await create.ExecuteNonQueryAsync();
            }
        }

        // The table exists in BOTH databases, so a load through the wrong one finds nothing rather than failing
        foreach (var connectionString in new[] { Servers.PostgresConnectionString, theTenantConnectionString })
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                create schema if not exists step_entity;
                drop table if exists step_entity.step_entity_items;
                create table step_entity.step_entity_items ("Id" uuid primary key, "Name" text not null);
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder();

        builder.Services.AddWolverineHttp();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(StepEntityRenameEndpoint).Assembly;
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Discovery.DisableConventionalDiscovery();

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "step_entity_wolverine")
                .RegisterStaticTenants(tenants => tenants.Register("red", theTenantConnectionString));

            opts.Services.AddDbContextWithWolverineManagedMultiTenancy<StepEntityDbContext>(
                (options, connectionString, _) => options.UseNpgsql(connectionString.Value), AutoCreate.None);

            opts.UseEntityFrameworkCoreTransactions();

            opts.Policies.AutoApplyTransactions();
            opts.Services.AddResourceSetupOnStartup();
        });

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.TenantId.IsQueryStringValue("tenant");

                opts.CustomizeHttpEndpointDiscovery(q =>
                {
                    q.Excludes.WithCondition("Not a step-entity endpoint", type => !EndpointTypes.Contains(type));
                    q.Includes.WithCondition("Step-entity endpoint", type => EndpointTypes.Contains(type));
                });
            });
        });
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> seedInTenantDatabaseAsync()
    {
        var id = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(theTenantConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """insert into step_entity.step_entity_items ("Id", "Name") values ($1, 'original')""", conn);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<string?> nameInTenantDatabaseAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(theTenantConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """select "Name" from step_entity.step_entity_items where "Id" = $1""", conn);
        cmd.Parameters.AddWithValue(id);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task entity_on_a_step_parameter_saves_the_mutation_to_the_tenant_database()
    {
        var id = await seedInTenantDatabaseAsync();

        await theHost.Scenario(x =>
        {
            x.Post.Url($"/step-entity/{id}/rename?tenant=red");
            x.StatusCodeShouldBe(204);
        });

        (await nameInTenantDatabaseAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task entity_on_the_endpoint_parameter_still_saves_the_mutation()
    {
        var id = await seedInTenantDatabaseAsync();

        await theHost.Scenario(x =>
        {
            x.Post.Url($"/step-entity/{id}/rename-on-endpoint?tenant=red");
            x.StatusCodeShouldBe(204);
        });

        (await nameInTenantDatabaseAsync(id)).ShouldBe("renamed");
    }
}
