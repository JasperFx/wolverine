using JasperFx.Core;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Spectre.Console;
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
    public static string UsingEnumQuerystring([FromQuery(Name = "name")] string value)
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

    [WolverineGet("/querystring/datetime")]
    public static string DateTime(DateTime value)
    {
        return value.ToString("O");
    }

    [WolverineGet("/querystring/datetime/nullable")]
    public static string DateTimeNullable(DateTime? value)
    {
        return value.HasValue ? value.Value.ToString("O") : "Value is missing";
    }

    [WolverineGet("/querystring/dateonly")]
    public static string DateOnly(DateOnly value)
    {
        return value.ToString("O");
    }

    [WolverineGet("/querystring/dateonly/nullable")]
    public static string DateOnlyNullable(DateOnly? value)
    {
        return value.HasValue ? value.Value.ToString("O") : "Value is missing";
    }
}

public static class FromQueryEndpoints
{
    [WolverineGet("/api/fromquery1")]
    public static Query1 Get([FromQuery] Query1 query) => query;

    [WolverineGet("/api/fromquery2")]
    public static Query2 Get([FromQuery] Query2 query) => query;

    [WolverineGet("/api/fromquery3")]
    public static Query3 Get([FromQuery] Query3 query) => query;

    [WolverineGet("/api/fromquery4")]
    public static Query4 Get([FromQuery] Query4 query) => query;

    [WolverineGet("/api/bigquery")]
    public static BigQuery Get([FromQuery] BigQuery query) => query;
}

public record Query1(string Name);
public record Query2(int Number);
public record Query3(Guid Id);

public record Query4(string Name, int Number, Direction Direction);

public class BigQuery
{
    public string Name { get; set; }
    public int Number { get; set; }
    public Direction Direction { get; set; }
    public string[] Values { get; set; }
    public int[] Numbers { get; set; }

    public bool Flag { get; set; }

    public int? NullableNumber { get; set; }
    public Direction? NullableDirection { get; set; }
    public bool? NullableFlag { get; set; }

    [FromQuery(Name = "aliased")]
    public string? ValueWithAlias { get; set; }
}