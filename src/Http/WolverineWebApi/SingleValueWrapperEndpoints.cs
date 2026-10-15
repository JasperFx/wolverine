using JasperFx.Core;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

// GH-4978: hand-written strong-typed ids with no TryParse and no JSON converter of their own

#region sample_single_value_wrapper_binding

public readonly record struct WidgetId(Guid Value);

public readonly record struct WidgetCode(string Value);

public static class WidgetEndpoints
{
    // Binds the route value straight to WidgetId by parsing the Guid inside it
    [WolverineGet("/widgets/{id}")]
    public static string GetWidget(WidgetId id) => id.Value.ToString();
}

#endregion

public readonly record struct WidgetNumber(int Value);

public static class SingleValueWrapperEndpoints
{
    [WolverineGet("/svw/route/string/{code}")]
    public static string RouteString(WidgetCode code) => code.Value;

    [WolverineGet("/svw/query/guid")]
    public static string QueryGuid(WidgetId id) => id.Value.ToString();

    [WolverineGet("/svw/query/nullable")]
    public static string QueryNullable(WidgetId? id) => id?.Value.ToString() ?? "none";

    [WolverineGet("/svw/query/string")]
    public static string QueryString(WidgetCode code) => code.Value ?? "none";

    [WolverineGet("/svw/query/array")]
    public static string QueryArray(WidgetNumber[]? numbers)
    {
        if (numbers == null || numbers.Length == 0) return "none";
        return numbers.Select(x => x.Value).OrderBy(x => x).Select(x => x.ToString()).Join(",");
    }

    [WolverineGet("/svw/query/list")]
    public static string QueryList(List<WidgetNumber> numbers)
    {
        return numbers.Count == 0
            ? "none"
            : numbers.Select(x => x.Value).OrderBy(x => x).Select(x => x.ToString()).Join(",");
    }

    [WolverinePost("/svw/form/guid")]
    public static string FormGuid([FromForm] WidgetId id) => id.Value.ToString();

    [WolverinePost("/svw/form/array")]
    public static string FormArray([FromForm] WidgetNumber[]? numbers)
    {
        if (numbers == null || numbers.Length == 0) return "none";
        return numbers.Select(x => x.Value).OrderBy(x => x).Select(x => x.ToString()).Join(",");
    }
}
