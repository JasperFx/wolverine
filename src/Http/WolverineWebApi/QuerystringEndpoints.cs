using JasperFx.Core;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

public static class QuerystringEndpoints
{

    [WolverineGet("/querystring/enum")]
    public static string UsingEnumQuerystring(Direction direction)
    {
        return direction.ToString();
    }

    [WolverineGet("/querystring/explicit")]
    public static string UsingEnumQuerystring([FromQuery(Name = "name")]string value)
    {
        return value ?? "";
    }

    [WolverineGet("/querystring/enum/nullable")]
    public static string UsingNullableEnumQuerystring(Direction? direction)
    {
        return direction?.ToString() ?? "none";
    }

    [WolverineGet("/querystring/stringarray")]
    public static string StringArray(string[]? values)
    {
        if (values == null || values.IsEmpty()) return "none";

        return values.Join(",");
    }

    [WolverineGet("/querystring/intarray")]
    public static string IntArray(int[]? values)
    {
        if (values == null || values.IsEmpty()) return "none";

        return values.OrderBy(x => x).Select(x => x.ToString()).Join(",");
    }
}