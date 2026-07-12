using Alba;
using IntegrationTests;
using JasperFx;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.Http.Tests.EfCoreOnly;
using Xunit;

namespace Wolverine.Http.Tests;

// Reproducer for GH-3374. An HTTP endpoint using [AsParameters] combined with a
// compound-handler LoadAsync that binds the same route variable had the route-binding
// frames emitted once per consuming method scope instead of once per chain. The
// duplicated locals (order_id_rawValue / order_id, plus the [AsParameters] container
// variable itself in the second variant) meant the generated class did not compile and
// the host failed at startup with "Compilation failures!".
//
// Like GH-3353/GH-3358, the endpoints live in the Marten-free Wolverine.Http.Tests.EfCoreOnly
// assembly and endpoint discovery is pinned there, because the main test assembly's endpoints
// assume Marten is registered.
public class Bug_3374_asparameters_with_loadasync_route_binding : IAsyncLifetime
{
    private IAlbaHost _host = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddDbContextWithWolverineIntegration<Bug3374DbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        // The EfCoreOnly assembly also holds the GH-3353/GH-3358 endpoints, which need their
        // own DbContext registered for endpoint discovery to succeed in this host
        builder.Services.AddDbContextWithWolverineIntegration<Bug3353DbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Host.UseWolverine(opts =>
        {
            // Pin endpoint discovery to the EF-Core-only assembly; see the class comment
            opts.ApplicationAssembly = typeof(Bug3374RouteLoadEndpoint).Assembly;

            opts.Durability.Mode = DurabilityMode.Solo;

            // The Wolverine-integrated DbContexts pull DatabaseSettings from the message store
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug3374");

            // Registers the EF Core persistence frame provider, which both resolves the
            // LoadAsync compound handlers here and satisfies the sibling GH-3353 endpoint's
            // storage action during endpoint discovery
            opts.UseEntityFrameworkCoreTransactions();

            opts.Discovery.DisableConventionalDiscovery();
        });

        builder.Services.AddWolverineHttp();

        // Before the fix this was already enough to fail intermittently depending on codegen
        // mode, but the deterministic failure is triggered by exercising the endpoints below
        _host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints());

        // The chains never write, so create the backing table by hand and seed the order rows
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Bug3374DbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            create schema if not exists bug3374;
            create table if not exists bug3374.bug3374_orders ("Id" bigint primary key, "Name" varchar(200) not null);
            delete from bug3374.bug3374_orders;
            insert into bug3374.bug3374_orders ("Id", "Name") values (42, 'route-load'), (43, 'asparameters-load');
            """);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task loadasync_binding_the_raw_route_value_compiles_and_runs()
    {
        // Executing the endpoint forces the codegen + compilation that blew up before the fix
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new Bug3374LinesBody(["one", "two", "three"]))
                .ToUrl("/bug3374/route-load/orders/42/lines");
            x.StatusCodeShouldBeOk();
        });

        var response = await result.ReadAsJsonAsync<Bug3374Response>();
        response.ShouldNotBeNull();
        response.OrderId.ShouldBe(42);
        response.OrderName.ShouldBe("route-load");
        response.LineCount.ShouldBe(3);
    }

    [Fact]
    public async Task loadasync_binding_the_same_asparameters_container_compiles_and_runs()
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new Bug3374LinesBody(["one", "two"]))
                .ToUrl("/bug3374/asparameters-load/orders/43/lines");
            x.StatusCodeShouldBeOk();
        });

        var response = await result.ReadAsJsonAsync<Bug3374Response>();
        response.ShouldNotBeNull();
        response.OrderId.ShouldBe(43);
        response.OrderName.ShouldBe("asparameters-load");
        response.LineCount.ShouldBe(2);
    }

    [Fact]
    public async Task missing_order_id_route_value_still_404s()
    {
        // Guard: an unparseable order id still 404s. The :long route constraint rejects this
        // at the routing layer; the generated route-binding frame (now emitted inside the
        // [AsParameters] binding block) keeps its own 404 short-circuit as the second line of
        // defense, which the compilation of the two endpoints above already proves out.
        await _host.Scenario(x =>
        {
            x.Put.Json(new Bug3374LinesBody(["one"]))
                .ToUrl("/bug3374/route-load/orders/not-a-number/lines");
            x.StatusCodeShouldBe(404);
        });
    }
}
