using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

// GH-5099: a value missing from the request binds the parameter's declared C# default, as in Minimal APIs

public static class DeclaredDefaultValueEndpoints
{
    // GET /defaults/query binds limit = 100 and name = "all"; GET /defaults/query?limit=5&name=x binds 5 and "x"
    [WolverineGet("/defaults/query")]
    public static string Query(int limit = 100, string name = "all", bool active = true,
        Direction direction = Direction.South, double ratio = 1.5, short offset = -3, int? page = 2)
        => $"limit={limit} name={name} active={active} direction={direction} " +
           $"ratio={ratio.ToString(CultureInfo.InvariantCulture)} offset={offset} page={page}";

    [WolverineGet("/defaults/query/named")]
    public static string Named([FromQuery(Name = "max")] int limit = 25, string quoted = "say \"hi\"\\\t")
        => $"limit={limit} quoted={quoted}";

    [WolverineGet("/defaults/header")]
    public static string Header([FromHeader(Name = "x-limit")] int limit = 10,
        [FromHeader(Name = "x-name")] string name = "anonymous")
        => $"limit={limit} name={name}";

    [WolverinePost("/defaults/form")]
    public static string Form([FromForm] int limit = 7, [FromForm] string name = "form")
        => $"limit={limit} name={name}";

    [WolverineGet("/defaults/asparameters")]
    public static string AsParameters([AsParameters] DefaultedQuery query)
        => $"limit={query.Limit} name={query.Name}";
}

public record DefaultedQuery([FromQuery] int Limit = 50, [FromQuery] string Name = "query");

