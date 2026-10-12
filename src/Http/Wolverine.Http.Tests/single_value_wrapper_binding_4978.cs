using Alba;
using Shouldly;
using WolverineWebApi;
using MvcBindingSource = Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource;

namespace Wolverine.Http.Tests;

/// <summary>
///     GH-4978. A hand-written single-value wrapper like <c>record struct WidgetId(Guid Value)</c> has no TryParse,
///     so it used to bind from nothing at all. It now binds by parsing the inner value and calling its constructor,
///     wherever a route, query string or form value can bind.
/// </summary>
public class single_value_wrapper_binding_4978 : IntegrationContext
{
    public single_value_wrapper_binding_4978(AppFixture fixture) : base(fixture)
    {
    }

    private async Task<string> getText(string url)
    {
        var result = await Scenario(x => x.Get.Url(url));
        return await result.ReadAsTextAsync();
    }

    private async Task<string> postForm(string url, IEnumerable<KeyValuePair<string, string>> form)
    {
        var result = await Scenario(x => x.Post.FormData(form.ToDictionary(p => p.Key, p => p.Value)).ToUrl(url));
        return await result.ReadAsTextAsync();
    }

    [Fact]
    public async Task route_value_around_a_guid()
    {
        var id = Guid.NewGuid();
        (await getText("/widgets/" + id)).ShouldBe(id.ToString());
    }

    [Fact]
    public async Task route_value_that_does_not_parse_is_a_404()
    {
        await Scenario(x =>
        {
            x.Get.Url("/widgets/not-a-guid");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task route_value_around_a_string()
    {
        (await getText("/svw/route/string/blue-widget")).ShouldBe("blue-widget");
    }

    [Fact]
    public async Task query_string_around_a_guid()
    {
        var id = Guid.NewGuid();
        (await getText("/svw/query/guid?id=" + id)).ShouldBe(id.ToString());
    }

    [Fact]
    public async Task nullable_query_string_value()
    {
        var id = Guid.NewGuid();
        (await getText("/svw/query/nullable?id=" + id)).ShouldBe(id.ToString());
        (await getText("/svw/query/nullable")).ShouldBe("none");
    }

    [Fact]
    public async Task query_string_around_a_string()
    {
        (await getText("/svw/query/string?code=red")).ShouldBe("red");
        (await getText("/svw/query/string")).ShouldBe("none");
    }

    [Fact]
    public async Task query_string_array()
    {
        (await getText("/svw/query/array?numbers=3&numbers=1&numbers=2")).ShouldBe("1,2,3");
        (await getText("/svw/query/array")).ShouldBe("none");
    }

    [Fact]
    public async Task query_string_list()
    {
        (await getText("/svw/query/list?numbers=3&numbers=1")).ShouldBe("1,3");
    }

    [Fact]
    public async Task form_value_around_a_guid()
    {
        var id = Guid.NewGuid();
        (await postForm("/svw/form/guid", [new("id", id.ToString())])).ShouldBe(id.ToString());
    }

    [Fact]
    public async Task form_array()
    {
        // FormData() takes one value per key, which is all this binder needs to show it parses
        (await postForm("/svw/form/array", [new("numbers", "7")])).ShouldBe("7");
    }

    [Fact]
    public void open_api_describes_the_inner_type()
    {
        var route = HttpChains.ChainFor("GET", "/widgets/{id}")!.CreateApiDescription("GET")
            .ParameterDescriptions.Single(x => x.Name == "id");
        route.Source.ShouldBe(MvcBindingSource.Path);
        route.Type.ShouldBe(typeof(Guid));

        var nullable = HttpChains.ChainFor("GET", "/svw/query/nullable")!.CreateApiDescription("GET")
            .ParameterDescriptions.Single(x => x.Name == "id");
        nullable.Type.ShouldBe(typeof(Guid?));

        var array = HttpChains.ChainFor("GET", "/svw/query/array")!.CreateApiDescription("GET")
            .ParameterDescriptions.Single(x => x.Name == "numbers");
        array.Type.ShouldBe(typeof(int[]));

        var form = HttpChains.ChainFor("POST", "/svw/form/guid")!.CreateApiDescription("POST")
            .ParameterDescriptions.Single(x => x.Name == "id");
        form.Type.ShouldBe(typeof(Guid));
        form.ParameterDescriptor.ParameterType.ShouldBe(typeof(Guid));
    }
}
