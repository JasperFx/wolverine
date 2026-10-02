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

// A [FromQuerySpecification] plan that projects into a non-entity class under conjoined tenancy has to read
// through the request tenant's DbContext, whether or not the chain is transactional
public class conjoined_projection_query_plan : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS conjoined_projection CASCADE";
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder();

        builder.Services.AddWolverineHttp();

        builder.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedProjectionDbContext>(
            (options, connectionString) => options.UseNpgsql(connectionString.Value),
            AutoCreate.CreateOrUpdate);

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(ConjoinedProjectionEndpoints).Assembly;
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_projection_wolverine");
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

                x.CustomizeHttpEndpointDiscovery(q =>
                {
                    q.Excludes.WithCondition("Not the conjoined projection endpoints",
                        type => type != typeof(ConjoinedProjectionEndpoints));
                    q.Includes.WithCondition("Conjoined projection endpoints",
                        type => type == typeof(ConjoinedProjectionEndpoints));
                });
            });
        });
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> createMemberAsync(string tenant, string name, string email)
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new CreateProjectionMember(id, name, email)).ToUrl("/conjoined-projection/members");
            x.WithRequestHeader("tenant", tenant);
            x.StatusCodeShouldBe(204);
        });

        return id;
    }

    private async Task<string> readAsync(string url)
    {
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url(url);
            x.StatusCodeShouldBe(200);
        });

        return await result.ReadAsTextAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/non-transactional")]
    public async Task a_projection_plan_reads_only_the_request_tenants_rows(string suffix)
    {
        var red = await createMemberAsync("red", "Rita", "rita@red.test");
        var blue = await createMemberAsync("blue", "Bram", "bram@blue.test");

        (await readAsync($"/conjoined-projection/members/{red}{suffix}?tenant=red")).ShouldBe("Rita <rita@red.test>");
        (await readAsync($"/conjoined-projection/members/{blue}{suffix}?tenant=blue")).ShouldBe("Bram <bram@blue.test>");

        (await readAsync($"/conjoined-projection/members/{red}{suffix}?tenant=blue")).ShouldBe("missing");
        (await readAsync($"/conjoined-projection/members/{blue}{suffix}?tenant=red")).ShouldBe("missing");
    }
}
