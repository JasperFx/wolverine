using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace Wolverine.Http.Tests;

public class query_string_collections_with_non_identifier_names
{
    [Fact]
    public async Task string_array_with_a_dashed_name()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Get.Url("/query-names/string-array?tag-ids=a&tag-ids=b");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("a,b");
    }

    [Fact]
    public async Task int_array_with_a_dashed_name()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Get.Url("/query-names/int-array?page-numbers=1&page-numbers=2");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("1,2");
    }

    [Fact]
    public async Task int_list_with_a_dotted_name()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Get.Url("/query-names/int-list?status.codes=200&status.codes=404");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("200,404");
    }

    [Fact]
    public async Task collection_properties_with_dashed_names_on_a_query_type()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Get.Url("/query-names/query-type?item-ids=3&item-ids=4&page-sizes=10&page-sizes=20");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("3,4|10,20");
    }

    private static async Task<IAlbaHost> startHostAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
            opts.ApplicationAssembly = typeof(query_string_collections_with_non_identifier_names).Assembly;
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(NonIdentifierQueryNameEndpoints));
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the query name endpoints",
                    type => type != typeof(NonIdentifierQueryNameEndpoints)))));
    }
}

public class NonIdentifierQueryNameQuery
{
    [FromQuery(Name = "item-ids")]
    public int[] ItemIds { get; set; } = [];

    [FromQuery(Name = "page-sizes")]
    public List<int> PageSizes { get; set; } = [];
}

public static class NonIdentifierQueryNameEndpoints
{
    [WolverineGet("/query-names/string-array")]
    public static string StringArray([FromQuery(Name = "tag-ids")] string[] tagIds) => string.Join(",", tagIds);

    [WolverineGet("/query-names/int-array")]
    public static string IntArray([FromQuery(Name = "page-numbers")] int[] pageNumbers) =>
        string.Join(",", pageNumbers);

    [WolverineGet("/query-names/int-list")]
    public static string IntList([FromQuery(Name = "status.codes")] List<int> statusCodes) =>
        string.Join(",", statusCodes);

    [WolverineGet("/query-names/query-type")]
    public static string QueryType([FromQuery] NonIdentifierQueryNameQuery query) =>
        $"{string.Join(",", query.ItemIds)}|{string.Join(",", query.PageSizes)}";
}
