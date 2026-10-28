using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Shouldly;
using Wolverine.Http.Tests.Bugs;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

// The header counterpart of strict_query_string_binding. A [FromHeader] value that cannot be parsed
// silently bound the parameter's default; WolverineHttpOptions.RejectUnparseableHeaderValues opts into
// a 400 ProblemDetails instead, as minimal APIs do. Since #5250 a repeated header binds as its
// comma-joined values, so "x-number: 1" sent twice arrives as "1,2" and is rejected the same way.
public class StrictHeaderBindingFixture : IAsyncLifetime
{
    public IAlbaHost StrictHost { get; private set; } = null!;
    public IAlbaHost LenientHost { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        StrictHost = await buildHost(rejectUnparseable: true);
        LenientHost = await buildHost(rejectUnparseable: false);
    }

    public async ValueTask DisposeAsync()
    {
        await StrictHost.DisposeAsync();
        await LenientHost.DisposeAsync();
    }

    private static Task<IAlbaHost> buildHost(bool rejectUnparseable)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Services.AddScoped<IUserService, UserService>();

        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(StrictHeaderBindingFixture).Assembly);
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
        });

        builder.Services.AddWolverineHttp();

        return AlbaHost.For(builder, app =>
        {
            #region sample_reject_unparseable_header_values
            app.MapWolverineEndpoints(opts =>
            {
                // Answer 400 with ProblemDetails when a [FromHeader] value can't be parsed
                opts.RejectUnparseableHeaderValues = rejectUnparseable;
            });
            #endregion
        }).StartAsync();
    }
}

public class strict_header_binding : IClassFixture<StrictHeaderBindingFixture>
{
    private readonly StrictHeaderBindingFixture _fixture;

    public strict_header_binding(StrictHeaderBindingFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<string> expect400(string url, string header, StringValues value)
    {
        var result = await _fixture.StrictHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.Request.Headers[header] = value);
            x.Get.Url(url);
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });

        return await result.ReadAsTextAsync();
    }

    [Fact]
    public async Task malformed_int_header_returns_400_naming_the_header()
    {
        var text = await expect400("/strict/header/int", "x-number", "abc");

        text.ShouldContain("Invalid header value");
        text.ShouldContain("x-number");
        text.ShouldContain("abc");
    }

    [Fact]
    public async Task a_repeated_int_header_returns_400()
    {
        var text = await expect400("/strict/header/int", "x-number", new StringValues(["1", "2"]));

        text.ShouldContain("1,2");
    }

    [Fact]
    public async Task malformed_nullable_guid_header_returns_400()
    {
        await expect400("/strict/header/guid", "x-token", "not-a-guid");
    }

    [Fact]
    public async Task malformed_enum_header_on_as_parameters_returns_400()
    {
        await expect400("/strict/header/asparameters", "x-sort", "sideways");
    }

    [Fact]
    public async Task malformed_header_read_by_middleware_returns_400_before_the_endpoint_runs()
    {
        StrictHeaderMiddlewareEndpoint.EndpointRan = false;

        await expect400("/strict/header/middleware", "x-tenant-number", "abc");

        StrictHeaderMiddlewareEndpoint.EndpointRan.ShouldBeFalse();
    }

    [Fact]
    public async Task valid_headers_bind_in_strict_mode()
    {
        await _fixture.StrictHost.Scenario(x =>
        {
            x.WithRequestHeader("x-number", "21");
            x.Get.Url("/strict/header/int");
            x.ContentShouldBe("42");
        });

        await _fixture.StrictHost.Scenario(x =>
        {
            x.WithRequestHeader("x-sort", "Descending");
            x.Get.Url("/strict/header/asparameters");
            x.ContentShouldBe("Descending");
        });
    }

    [Fact]
    public async Task missing_headers_keep_their_defaults_in_strict_mode()
    {
        await _fixture.StrictHost.Scenario(x =>
        {
            x.Get.Url("/strict/header/int");
            x.ContentShouldBe("0");
        });

        await _fixture.StrictHost.Scenario(x =>
        {
            x.Get.Url("/strict/header/guid");
            x.ContentShouldBe("none");
        });

        await _fixture.StrictHost.Scenario(x =>
        {
            x.Get.Url("/strict/header/asparameters");
            x.ContentShouldBe("Ascending");
        });
    }

    [Fact]
    public async Task string_headers_are_never_rejected()
    {
        await _fixture.StrictHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.Request.Headers["x-name"] = new StringValues(["one", "two"]));
            x.Get.Url("/strict/header/string");
            x.ContentShouldBe("one,two");
        });
    }

    [Fact]
    public async Task malformed_headers_bind_their_defaults_when_the_flag_is_off()
    {
        await _fixture.LenientHost.Scenario(x =>
        {
            x.WithRequestHeader("x-number", "abc");
            x.Get.Url("/strict/header/int");
            x.ContentShouldBe("0");
        });

        await _fixture.LenientHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.Request.Headers["x-number"] = new StringValues(["1", "2"]));
            x.Get.Url("/strict/header/int");
            x.ContentShouldBe("0");
        });

        await _fixture.LenientHost.Scenario(x =>
        {
            x.WithRequestHeader("x-sort", "sideways");
            x.Get.Url("/strict/header/asparameters");
            x.ContentShouldBe("Ascending");
        });
    }
}

public enum StrictHeaderSort
{
    Ascending,
    Descending
}

public class StrictHeaderParameters
{
    [FromHeader(Name = "x-sort")]
    public StrictHeaderSort Sort { get; set; } = StrictHeaderSort.Ascending;
}

public static class StrictHeaderEndpoints
{
    [WolverineGet("/strict/header/int")]
    public static string Number([FromHeader(Name = "x-number")] int number) => (number * 2).ToString();

    [WolverineGet("/strict/header/guid")]
    public static string Token([FromHeader(Name = "x-token")] Guid? token) => token?.ToString() ?? "none";

    [WolverineGet("/strict/header/string")]
    public static string Name([FromHeader(Name = "x-name")] string? name) => name ?? "none";

    [WolverineGet("/strict/header/asparameters")]
    public static string Sorted([AsParameters] StrictHeaderParameters parameters) => parameters.Sort.ToString();
}

public static class StrictHeaderMiddlewareEndpoint
{
    public static bool EndpointRan;

    public static void Before([FromHeader(Name = "x-tenant-number")] int tenantNumber)
    {
    }

    [WolverineGet("/strict/header/middleware")]
    public static string Get()
    {
        EndpointRan = true;
        return "ran";
    }
}
