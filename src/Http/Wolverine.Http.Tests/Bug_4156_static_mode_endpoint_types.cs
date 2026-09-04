using JasperFx.CodeGeneration;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Tests.DifferentAssembly.Validation;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4156. GH-4151 made <c>HandlerGraph</c> assert at startup, in <see cref="TypeLoadMode.Static" />, that
/// every handler chain's pre-generated type really is in <see cref="WolverineOptions.ApplicationAssembly" />
/// -- because `codegen write` emits into the ENTRY project, and when ApplicationAssembly points somewhere
/// else the two disagree with nothing to detect it.
///
/// <para>Wolverine.Http's endpoint chains are a separate <c>ICodeFileCollection</c> and were deliberately
/// out of that PR's scope, with the identical exposure: in Static mode there is no fallback, so a missing
/// pre-generated endpoint type surfaced on the first HTTP request to that route -- a 500 per request on a
/// host that had reported healthy since the deploy, with no failure policy able to reach it.</para>
/// </summary>
public class Bug_4156_static_mode_endpoint_types
{
    [Fact]
    public async Task static_mode_without_pre_built_endpoint_types_fails_the_mapping()
    {
        await using var app = buildHost(TypeLoadMode.Static);

        // No pre-built types were ever generated into DifferentAssembly, which is exactly the state the
        // entry-project-vs-library split leaves a Static mode app in.
        var ex = Should.Throw<MissingPreBuiltTypesException>(() => app.MapWolverineEndpoints());

        // Name the assembly that was searched, because "the types are missing" and "the types are in the
        // other assembly" call for different fixes.
        ex.Message.ShouldContain(typeof(Validated2Endpoint).Assembly.GetName().Name!);

        // And name the ROUTES, not just the generated type names. An operator reading a failed deploy knows
        // which routes they have and does not know what codegen called them.
        ex.Message.ShouldContain("validate2/customer");
    }

    [Fact]
    public async Task auto_mode_maps_the_same_endpoints_without_complaint()
    {
        // False-positive guard, and the working configuration for this layout: Auto generates what it
        // cannot load, so the assembly split costs a cold start rather than correctness.
        await using var app = buildHost(TypeLoadMode.Auto);

        Should.NotThrow(() => app.MapWolverineEndpoints());
    }

    private static WebApplication buildHost(TypeLoadMode mode)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();

        // "DifferentAssembly" carries Marten aggregate endpoints, so any host discovering it needs Marten
        // registered to resolve the aggregates' id types. Nothing here starts the host, so point it at an
        // unreachable database to keep the test free of infrastructure.
        builder.Services.AddMarten(opts =>
        {
            opts.Connection(
                "Host=localhost;Port=9999;Database=does_not_exist;Username=nobody;Password=nobody;Timeout=2;Command Timeout=2");
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            // Pin discovery to the small, isolated assembly so the test is deterministic and does not pick
            // up the rest of the suite's endpoints.
            opts.ApplicationAssembly = typeof(Validated2Endpoint).Assembly;
            opts.CodeGeneration.TypeLoadMode = mode;
        });

        builder.Services.AddWolverineHttp();

        return builder.Build();
    }
}
