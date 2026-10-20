using Alba;
using Shouldly;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http.Tests;

/// <summary>
///     GH-5099. A parameter bound from the query string, a header, the form or an [AsParameters] constructor
///     used to bind the type's default (0, null) when the request omitted it, ignoring the declared C# default.
///     It now binds the declared default, as ASP.NET Core Minimal APIs do.
/// </summary>
public class declared_default_values_5099 : IntegrationContext
{
    public declared_default_values_5099(AppFixture fixture) : base(fixture)
    {
    }

    private async Task<string> getText(string url, Action<Scenario>? configure = null)
    {
        var result = await Scenario(x =>
        {
            x.Get.Url(url);
            configure?.Invoke(x);
        });

        return await result.ReadAsTextAsync();
    }

    [Fact]
    public async Task an_omitted_query_value_binds_the_declared_default()
    {
        (await getText("/defaults/query"))
            .ShouldBe("limit=100 name=all active=True direction=South ratio=1.5 offset=-3 page=2");
    }

    [Fact]
    public async Task a_query_value_that_is_present_still_wins()
    {
        (await getText("/defaults/query?limit=5&name=x&active=false&direction=north&ratio=0.25&offset=4&page=9"))
            .ShouldBe("limit=5 name=x active=False direction=North ratio=0.25 offset=4 page=9");
    }

    [Fact]
    public async Task an_unparseable_query_value_keeps_the_declared_default_in_lenient_mode()
    {
        // A failed TryParse must not clobber the declared default with the type's default
        (await getText("/defaults/query?limit=abc&active=maybe&direction=up"))
            .ShouldBe("limit=100 name=all active=True direction=South ratio=1.5 offset=-3 page=2");
    }

    [Fact]
    public async Task a_renamed_query_value_and_an_escaped_string_default()
    {
        (await getText("/defaults/query/named")).ShouldBe("limit=25 quoted=say \"hi\"\\\t");
        (await getText("/defaults/query/named?max=3&quoted=plain")).ShouldBe("limit=3 quoted=plain");
    }

    [Fact]
    public async Task an_omitted_header_binds_the_declared_default()
    {
        (await getText("/defaults/header")).ShouldBe("limit=10 name=anonymous");
        (await getText("/defaults/header", x =>
        {
            x.WithRequestHeader("x-limit", "4");
            x.WithRequestHeader("x-name", "bob");
        })).ShouldBe("limit=4 name=bob");
    }

    [Fact]
    public async Task an_omitted_form_value_binds_the_declared_default()
    {
        var result = await Scenario(x =>
            x.Post.FormData(new Dictionary<string, string> { ["other"] = "1" }).ToUrl("/defaults/form"));
        (await result.ReadAsTextAsync()).ShouldBe("limit=7 name=form");

        result = await Scenario(x =>
            x.Post.FormData(new Dictionary<string, string> { ["limit"] = "2", ["name"] = "posted" })
                .ToUrl("/defaults/form"));
        (await result.ReadAsTextAsync()).ShouldBe("limit=2 name=posted");
    }

    [Fact]
    public async Task an_as_parameters_constructor_parameter_keeps_its_declared_default()
    {
        (await getText("/defaults/asparameters")).ShouldBe("limit=50 name=query");
        (await getText("/defaults/asparameters?limit=1&name=n")).ShouldBe("limit=1 name=n");
    }

    [Fact]
    public void open_api_advertises_the_declared_default()
    {
        var parameters = HttpChains.ChainFor("GET", "/defaults/query")!.CreateApiDescription("GET")
            .ParameterDescriptions;

        parameters.Single(x => x.Name == "limit").DefaultValue.ShouldBe(100);
        parameters.Single(x => x.Name == "name").DefaultValue.ShouldBe("all");
        parameters.Single(x => x.Name == "direction").DefaultValue.ShouldBe(WolverineWebApi.Direction.South);
    }

    [Theory]
    [InlineData(typeof(int), 100, "100")]
    [InlineData(typeof(long), -5L, "-5L")]
    [InlineData(typeof(short), (short)-3, "(System.Int16)(-3)")]
    [InlineData(typeof(bool), false, "false")]
    [InlineData(typeof(string), "a\"b\\c\n", "\"a\\\"b\\\\c\\n\"")]
    [InlineData(typeof(char), '\'', "'\\''")]
    [InlineData(typeof(double), 1.5, "1.5D")]
    public void literal_for_a_declared_default(Type type, object value, string expected)
    {
        DeclaredDefaultValue.TryWriteLiteral(type, value, out var code).ShouldBeTrue();
        code.ShouldBe(expected);
    }
}
