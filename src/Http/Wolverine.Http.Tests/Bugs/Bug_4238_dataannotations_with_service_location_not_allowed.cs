using System.ComponentModel.DataAnnotations;
using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using IntegrationTests;
using JasperFx.CodeGeneration.Model;
using Marten;
using Wolverine.Http.Validation;
using WolverineWebApi;

namespace Wolverine.Http.Tests.Bugs;

/// <summary>
/// GH-4238: the DataAnnotations validation middleware could not be used together with
/// <see cref="ServiceLocationPolicy.NotAllowed" />, which is the Wolverine 6 default.
///
/// <para>The executor takes an <see cref="IServiceProvider" /> so it can build the
/// <see cref="ValidationContext" />, and GH-4171 deliberately stopped <c>IServiceProvider</c> being
/// answered silently from <c>httpContext.RequestServices</c> as a derived variable — it now goes
/// through the normal service-variable machinery, which reports it as a service location. Correct for
/// user code, but it meant Wolverine's own middleware tripped the policy and the application could not
/// bootstrap at all.</para>
/// </summary>
public class Bug_4238_dataannotations_with_service_location_not_allowed
{
    private static async Task<IAlbaHost> hostAsync(ServiceLocationPolicy policy)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IgnoreAssembly(typeof(OpenApiEndpoints).Assembly);
            opts.Discovery.IncludeAssembly(typeof(Bug_4238_dataannotations_with_service_location_not_allowed).Assembly);

            opts.ServiceLocationPolicy = policy;

            // Endpoint discovery sweeps this whole assembly, and plenty of its endpoints are
            // Marten-backed. Nothing here uses Marten; it just has to resolve.
            opts.Services.AddMarten(Servers.PostgresConnectionString);
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts => opts.UseDataAnnotationsValidationProblemDetailMiddleware());
        });
    }

    [Fact]
    public async Task can_bootstrap_and_validate_with_service_location_not_allowed()
    {
        // The bug: this threw at bootstrap, so nothing below was reachable.
        using var host = await hostAsync(ServiceLocationPolicy.NotAllowed);

        await host.Scenario(x =>
        {
            x.Post.Json(new Bug4238Command("")).ToUrl("/bug4238/validate");
            x.StatusCodeShouldBe(400);
        });

        await host.Scenario(x =>
        {
            x.Post.Json(new Bug4238Command("ok")).ToUrl("/bug4238/validate");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task still_works_with_service_location_allowed()
    {
        // The control: the permissive policy was never broken, and must stay unbroken.
        using var host = await hostAsync(ServiceLocationPolicy.AlwaysAllowed);

        await host.Scenario(x =>
        {
            x.Post.Json(new Bug4238Command("")).ToUrl("/bug4238/validate");
            x.StatusCodeShouldBe(400);
        });
    }
}

public record Bug4238Command([property: Required] string Name);

public static class Bug4238Endpoint
{
    [WolverinePost("/bug4238/validate")]
    public static string Post(Bug4238Command command) => "ok";
}
