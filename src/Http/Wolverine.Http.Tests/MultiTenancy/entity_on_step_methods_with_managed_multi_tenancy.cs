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
using Wolverine.Util;
using Xunit;

namespace Wolverine.Http.Tests.MultiTenancy;

// Managed multi-tenancy plus UseEntityFrameworkCoreTransactions(). Every row is seeded only in the tenant database,
// so reading through the main database shows up as a 404 or a missing row.
public class entity_on_step_methods_with_managed_multi_tenancy : IAsyncLifetime
{
    private const string TenantDatabase = "step_entity_red";

    private static readonly Type[] EndpointTypes =
    [
        typeof(StepEntityRenameEndpoint),
        typeof(StepEntityRenameOnEndpoint),
        typeof(StepEntityReadEndpoint),
        typeof(StepEntityQueryPlanEndpoint),
        typeof(StepEntityTwoPlansEndpoint),
        typeof(StepEntityTagsEndpoint),
        typeof(StepEntityScheduleEndpoint)
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

        foreach (var connectionString in new[] { Servers.PostgresConnectionString, theTenantConnectionString })
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                create schema if not exists step_entity;
                drop table if exists step_entity.step_entity_items;
                create table step_entity.step_entity_items ("Id" uuid primary key, "Name" text not null);
                drop table if exists step_entity.step_entity_tags;
                create table step_entity.step_entity_tags ("Id" uuid primary key, "Label" text not null);
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder();

        builder.Services.AddWolverineHttp();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(StepEntityRenameEndpoint).Assembly;
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(StepEntityReminderHandler));
            opts.Policies.UseDurableLocalQueues();

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "step_entity_wolverine")
                .RegisterStaticTenants(tenants => tenants.Register("red", theTenantConnectionString));

            opts.Services.AddDbContextWithWolverineManagedMultiTenancy<StepEntityDbContext>(
                (options, connectionString, _) => options.UseNpgsql(connectionString.Value), AutoCreate.None);

            // Registration order matters: this has to come after the multi-tenancy registration
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

    private async Task<Guid> seedTagInTenantDatabaseAsync()
    {
        var id = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(theTenantConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """insert into step_entity.step_entity_tags ("Id", "Label") values ($1, 'red tag')""", conn);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<long> scheduledRemindersInTenantDatabaseAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(theTenantConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            select count(*) from step_entity_wolverine.wolverine_incoming_envelopes
            where message_type = $1 and status = 'Scheduled' and position(convert_to($2, 'UTF8') in body) > 0
            """, conn);
        cmd.Parameters.AddWithValue(typeof(StepEntityReminder).ToMessageTypeName());

        cmd.Parameters.AddWithValue(id.ToString());
        return (long)(await cmd.ExecuteScalarAsync())!;
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

    [Fact]
    public async Task a_non_transactional_load_reads_the_tenant_database()
    {
        var id = await seedInTenantDatabaseAsync();

        var result = await theHost.Scenario(x =>
        {
            x.Get.Url($"/step-entity/{id}/name?tenant=red");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("original");
    }

    [Fact]
    public async Task a_query_plan_returned_from_load_compiles_and_reads_the_tenant_database()
    {
        var id = await seedInTenantDatabaseAsync();

        var result = await theHost.Scenario(x =>
        {
            x.Get.Url($"/step-entity/{id}/name-from-plan?tenant=red");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("original");
    }

    [Fact]
    public async Task two_batched_query_specifications_compile_and_save_to_the_tenant_database()
    {
        var id = await seedInTenantDatabaseAsync();

        await theHost.Scenario(x =>
        {
            x.Post.Url($"/step-entity/{id}/two-plans?tenant=red");
            x.StatusCodeShouldBe(204);
        });

        (await nameInTenantDatabaseAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task a_chain_taking_the_db_context_and_loading_through_plans_starts_and_reads_the_tenant_database()
    {
        var id = await seedTagInTenantDatabaseAsync();

        var result = await theHost.Scenario(x =>
        {
            x.Get.Url($"/step-entity/{id}/tags?tenant=red");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("red tag of 1");
    }

    [Fact]
    public async Task a_non_transactional_chain_taking_the_db_context_persists_a_scheduled_message()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Url($"/step-entity/{id}/schedule?tenant=red");
            x.StatusCodeShouldBe(204);
        });

        (await scheduledRemindersInTenantDatabaseAsync(id)).ShouldBe(1);
    }
}
