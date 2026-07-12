using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Tests.Bugs;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

// GH-3372: [AsParameters] / query string binding silently ignored unparseable values,
// leaving the property at its initializer where ASP.NET Core minimal APIs would 400.
// WolverineHttpOptions.RejectUnparseableQueryValues opts into the strict (minimal API)
// behavior; the default flips to strict in Wolverine 7.0.
public class StrictQueryBindingFixture : IAsyncLifetime
{
    public IAlbaHost StrictHost { get; private set; } = null!;
    public IAlbaHost LenientHost { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        StrictHost = await buildHost(rejectUnparseable: true);
        LenientHost = await buildHost(rejectUnparseable: false);
    }

    public async Task DisposeAsync()
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
            opts.Discovery.IncludeAssembly(typeof(StrictQueryBindingFixture).Assembly);
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
        });

        builder.Services.AddWolverineHttp();

        return AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts => opts.RejectUnparseableQueryValues = rejectUnparseable);
        });
    }
}

public class strict_query_string_binding : IClassFixture<StrictQueryBindingFixture>
{
    private readonly StrictQueryBindingFixture _fixture;

    public strict_query_string_binding(StrictQueryBindingFixture fixture)
    {
        _fixture = fixture;
    }

    private Task<IScenarioResult> expect400(string key, string value)
    {
        return _fixture.StrictHost.Scenario(x =>
        {
            x.Get.Url("/strict/asparameters").QueryString(key, value);
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }

    [Fact]
    public async Task malformed_enum_returns_400_with_problem_details_naming_the_parameter()
    {
        var result = await expect400("sort-by", "bogus");

        var text = await result.ReadAsTextAsync();
        text.ShouldContain("sort-by");
        text.ShouldContain("bogus");
    }

    [Fact]
    public async Task malformed_int_returns_400_with_problem_details_naming_the_parameter()
    {
        var result = await expect400("PageSize", "abc");

        var text = await result.ReadAsTextAsync();
        text.ShouldContain("PageSize");
    }

    [Fact]
    public async Task malformed_guid_returns_400_with_problem_details_naming_the_parameter()
    {
        var result = await expect400("Token", "not-a-guid");

        var text = await result.ReadAsTextAsync();
        text.ShouldContain("Token");
    }

    [Fact]
    public async Task malformed_nullable_enum_returns_400()
    {
        await expect400("MaybeSort", "sideways");
    }

    [Fact]
    public async Task malformed_nullable_int_returns_400()
    {
        await expect400("MaybePage", "abc");
    }

    [Fact]
    public async Task malformed_nullable_guid_returns_400()
    {
        await expect400("MaybeToken", "not-a-guid");
    }

    [Fact]
    public async Task missing_values_keep_initializers_in_strict_mode()
    {
        var result = await _fixture.StrictHost.Scenario(x =>
        {
            x.Get.Url("/strict/asparameters");
            x.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<StrictQueryModel>();
        response.SortBy.ShouldBe(StrictSortOrder.Date);
        response.PageSize.ShouldBe(5);
        response.Token.ShouldBe(StrictQueryModel.DefaultToken);
        response.MaybeSort.ShouldBeNull();
        response.MaybePage.ShouldBeNull();
        response.MaybeToken.ShouldBeNull();
    }

    [Fact]
    public async Task valid_values_bind_in_strict_mode()
    {
        var token = Guid.NewGuid();
        var maybeToken = Guid.NewGuid();

        var result = await _fixture.StrictHost.Scenario(x =>
        {
            x.Get.Url("/strict/asparameters")
                .QueryString("sort-by", "Name")
                .QueryString("PageSize", "20")
                .QueryString("Token", token.ToString())
                .QueryString("MaybeSort", "Date")
                .QueryString("MaybePage", "3")
                .QueryString("MaybeToken", maybeToken.ToString());
            x.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<StrictQueryModel>();
        response.SortBy.ShouldBe(StrictSortOrder.Name);
        response.PageSize.ShouldBe(20);
        response.Token.ShouldBe(token);
        response.MaybeSort.ShouldBe(StrictSortOrder.Date);
        response.MaybePage.ShouldBe(3);
        response.MaybeToken.ShouldBe(maybeToken);
    }

    [Fact]
    public async Task malformed_direct_method_argument_returns_400()
    {
        // The same generated query binder handles direct endpoint method arguments,
        // so the strict behavior covers those too
        var result = await _fixture.StrictHost.Scenario(x =>
        {
            x.Get.Url("/strict/direct").QueryString("number", "abc");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldContain("number");
    }

    [Fact]
    public async Task valid_direct_method_argument_binds_in_strict_mode()
    {
        var result = await _fixture.StrictHost.Scenario(x =>
        {
            x.Get.Url("/strict/direct").QueryString("number", "42");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("42");
    }

    [Fact]
    public async Task missing_direct_method_argument_keeps_default_in_strict_mode()
    {
        var result = await _fixture.StrictHost.Scenario(x =>
        {
            x.Get.Url("/strict/direct");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("0");
    }

    [Fact]
    public async Task malformed_values_keep_initializers_when_flag_is_off()
    {
        // The pre-3372 lenient behavior is preserved by default in 6.x
        var result = await _fixture.LenientHost.Scenario(x =>
        {
            x.Get.Url("/strict/asparameters")
                .QueryString("sort-by", "bogus")
                .QueryString("PageSize", "abc")
                .QueryString("Token", "not-a-guid")
                .QueryString("MaybeSort", "sideways")
                .QueryString("MaybePage", "abc")
                .QueryString("MaybeToken", "not-a-guid");
            x.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<StrictQueryModel>();
        response.SortBy.ShouldBe(StrictSortOrder.Date);
        response.PageSize.ShouldBe(5);
        response.Token.ShouldBe(StrictQueryModel.DefaultToken);
        response.MaybeSort.ShouldBeNull();
        response.MaybePage.ShouldBeNull();
        response.MaybeToken.ShouldBeNull();
    }

    [Fact]
    public async Task missing_values_keep_initializers_when_flag_is_off()
    {
        var result = await _fixture.LenientHost.Scenario(x =>
        {
            x.Get.Url("/strict/asparameters");
            x.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<StrictQueryModel>();
        response.SortBy.ShouldBe(StrictSortOrder.Date);
        response.PageSize.ShouldBe(5);
        response.Token.ShouldBe(StrictQueryModel.DefaultToken);
    }
}

public enum StrictSortOrder
{
    Name = 1,
    Date = 2
}

public class StrictQueryModel
{
    public static readonly Guid DefaultToken = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [FromQuery(Name = "sort-by")]
    public StrictSortOrder SortBy { get; set; } = StrictSortOrder.Date;

    [FromQuery]
    public int PageSize { get; set; } = 5;

    [FromQuery]
    public Guid Token { get; set; } = DefaultToken;

    [FromQuery]
    public StrictSortOrder? MaybeSort { get; set; }

    [FromQuery]
    public int? MaybePage { get; set; }

    [FromQuery]
    public Guid? MaybeToken { get; set; }
}

public static class StrictQueryBindingEndpoints
{
    [WolverineGet("/strict/asparameters")]
    public static StrictQueryModel Get([AsParameters] StrictQueryModel query)
    {
        return query;
    }

    [WolverineGet("/strict/direct")]
    public static string GetDirect(int number)
    {
        return number.ToString();
    }
}
