using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http.Tests.EfCoreOnly;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Http.Tests.MultiTenancy;

// GH-3462/GH-3465 Phase 4: HTTP tenant detection flowing into conjoined EF Core
// multi-tenancy -- the detected tenant pins the DbContext, stamps writes, and
// scopes reads, with zero tenant-awareness in the endpoint code
public class conjoined_tenancy_http_detection : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS conjoined_http CASCADE";
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder();

        builder.Services.AddWolverineHttp();

        // The sibling endpoints in the EfCoreOnly assembly need their own DbContext
        // registered for chain building
        builder.Services.AddDbContextWithWolverineIntegration<Bug3353DbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedNotesDbContext>(
            (options, connectionString) => options.UseNpgsql(connectionString.Value),
            AutoCreate.CreateOrUpdate);

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(ConjoinedNotesEndpoint).Assembly;
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_http_wolverine");
            opts.UseEntityFrameworkCoreTransactions();
            opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            opts.Policies.AutoApplyTransactions();
            opts.Services.AddResourceSetupOnStartup();
            opts.Discovery.DisableConventionalDiscovery();
        });

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(x =>
            {
                x.TenantId.IsQueryStringValue("tenant");
                x.TenantId.IsRequestHeaderValue("tenant");
            });
        });
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task detected_tenant_stamps_writes_and_scopes_reads()
    {
        var greenNote = new CreateNote(Guid.NewGuid(), "green thoughts");
        var blueNote = new CreateNote(Guid.NewGuid(), "blue thoughts");

        await theHost.Scenario(x =>
        {
            x.Post.Json(greenNote).ToUrl("/conjoined/notes/create");
            x.WithRequestHeader("tenant", "green");
            x.StatusCodeShouldBe(204);
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(blueNote).ToUrl("/conjoined/notes/create");
            x.WithRequestHeader("tenant", "blue");
            x.StatusCodeShouldBe(204);
        });

        var greenResult = await theHost.GetAsJson<TenantedNote[]>("/conjoined/notes?tenant=green");
        greenResult.ShouldNotBeNull();
        greenResult.Single().Id.ShouldBe(greenNote.Id);
        greenResult.Single().TenantId.ShouldBe("green");

        var blueResult = await theHost.GetAsJson<TenantedNote[]>("/conjoined/notes?tenant=blue");
        blueResult.ShouldNotBeNull();
        blueResult.Single().Id.ShouldBe(blueNote.Id);
    }

    [Fact]
    public async Task post_endpoint_with_only_a_dbcontext_parameter_does_not_infer_it_as_the_body()
    {
        // GH-3538: posting with no request body must NOT 400 — the DbContext is a service
        // parameter, not the inferred JSON request body. Before the fix this returned 400
        // "Invalid JSON format"; the empty POST would fail body deserialization.
        await theHost.Scenario(x =>
        {
            x.Post.Url("/conjoined/notes/quick-add");
            x.WithRequestHeader("tenant", "green");
            x.StatusCodeShouldBe(204);
        });

        // The write actually landed for the tenant, proving the DbContext resolved as a service
        var notes = await theHost.GetAsJson<TenantedNote[]>("/conjoined/notes?tenant=green");
        notes.ShouldNotBeNull();
        notes!.ShouldContain(n => n.Text == "quick-add");
    }
}
