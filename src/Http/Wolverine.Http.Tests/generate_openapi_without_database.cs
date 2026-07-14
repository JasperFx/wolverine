using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Commands;
using Wolverine.Http.Tests.DifferentAssembly.Validation;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

// Proves GH-2903: the `openapi` command can generate the server OpenAPI document straight from
// endpoint metadata without starting the host — so an application backed by durable (database)
// message persistence produces its document with no database connectivity whatsoever.
public class generate_openapi_without_database
{
    [Fact]
    public async Task generates_full_document_with_no_database_connection()
    {
        var json = await GenerateDocumentWithoutDatabaseAsync();

        // A real OpenAPI document containing the discovered Wolverine endpoints.
        json.ShouldContain("\"openapi\"");
        json.ShouldContain("/validate2/customer");
        json.ShouldContain("/validate2/user");
    }

    // The command never starts the host, and ASP.NET Core does not surface an application's endpoints to
    // its own description providers until it does — so a hybrid host (Wolverine HTTP endpoints alongside
    // minimal API routes) is where a document quietly reduced to "Wolverine's routes only" would show up.
    // The command already merged the endpoint data sources itself to avoid that; GH-3421 lifted that merge
    // into HostEndpointDataSources so an in-process ApiExplorer read gets it too. Pin the behavior here,
    // now that the command depends on shared code to keep it.
    [Fact]
    public async Task generates_the_whole_route_table_on_a_hybrid_host()
    {
        var json = await GenerateDocumentWithoutDatabaseAsync(app =>
        {
            app.MapGet("/minimal/hello", () => "Hello");
        });

        json.ShouldContain("/validate2/customer");
        json.ShouldContain("/minimal/hello");
    }

    [Fact]
    public async Task route_filter_keeps_only_matching_routes_and_their_schemas()
    {
        var json = await GenerateDocumentWithoutDatabaseAsync();

        var filtered = OpenApiRouteFilter.Filter(json, "customer", out var matchedPaths);

        // Only the fuzzy-matched route survives...
        matchedPaths.ShouldBe(["/validate2/customer"]);
        filtered.ShouldContain("/validate2/customer");
        filtered.ShouldNotContain("/validate2/user");

        // ...along with the schema it references, while the unrelated schema is pruned.
        filtered.ShouldContain("CreateCustomer2");
        filtered.ShouldNotContain("CreateUser2");
    }

    [Fact]
    public async Task route_filter_with_no_match_reports_available_routes()
    {
        var json = await GenerateDocumentWithoutDatabaseAsync();

        OpenApiRouteFilter.Filter(json, "does-not-exist", out var matchedPaths);
        matchedPaths.ShouldBeEmpty();

        OpenApiRouteFilter.ListPaths(json).ShouldContain("/validate2/customer");
    }

    private static async Task<string> GenerateDocumentWithoutDatabaseAsync(Action<WebApplication>? configure = null)
    {
        // Build in the Development environment, which is what the CI runner uses and which turns on DI
        // scope validation by default. This reproduces GH-2911: the Microsoft.AspNetCore.OpenApi
        // document provider is a root-captured singleton, so the document must be generated without
        // resolving a scoped service from the root provider.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });

        // Configure database-backed Wolverine message persistence (Marten/PostgreSQL) pointed at an
        // unreachable database. Port 9999 has nothing listening, so if document generation ever
        // attempted to open a connection this test would fail fast (or hang) rather than pass.
        builder.Services
            .AddMarten(opts =>
            {
                opts.Connection(
                    "Host=localhost;Port=9999;Database=does_not_exist;Username=nobody;Password=nobody;Timeout=2;Command Timeout=2");
            })
            .IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            // Pin endpoint discovery to the small, isolated "DifferentAssembly" so the test is
            // deterministic and does not pick up the rest of the test suite's endpoints.
            opts.ApplicationAssembly = typeof(Validated2Endpoint).Assembly;

            // The durable-transaction policy is exactly the kind of database-coupled configuration
            // that would otherwise force a connection during a full host start.
            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
        });

        // The Microsoft.AspNetCore.OpenApi document provider that the command reuses.
        builder.Services.AddOpenApi();
        builder.Services.AddWolverineHttp();

        await using var app = builder.Build();
        app.MapWolverineEndpoints();

        configure?.Invoke(app);

        // Exercise the exact no-startup path the `openapi` command uses. Note: no app.StartAsync().
        var documentProvider = OpenApiCommand.PrepareDocumentProvider(app);
        documentProvider.ShouldNotBeNull();

        documentProvider!.GetDocumentNames().ShouldContain("v1");

        var writer = new StringWriter();
        await documentProvider.GenerateAsync("v1", writer);
        return writer.ToString();
    }
}
