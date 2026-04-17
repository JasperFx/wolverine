using System.Text;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;
using Wolverine.Http.ContentNegotiation;

namespace WolverineWebApi;

public record ConnegItem(string Name, int Value);

#region sample_conneg_write_response
/// <summary>
/// Demonstrates content negotiation with [Writes] attribute.
/// Multiple WriteResponse methods handle different content types.
/// </summary>
public static class ConnegWriteEndpoints
{
    [WolverineGet("/conneg/write")]
    public static ConnegItem GetItem()
    {
        return new ConnegItem("Widget", 42);
    }

    /// <summary>
    /// Writes the response as plain text when Accept: text/plain
    /// </summary>
    [Writes("text/plain")]
    public static Task WriteResponse(HttpContext context, ConnegItem response)
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync($"{response.Name}: {response.Value}");
    }

    /// <summary>
    /// Writes the response as CSV when Accept: text/csv
    /// </summary>
    [Writes("text/csv")]
    public static Task WriteResponseCsv(HttpContext context, ConnegItem response)
    {
        context.Response.ContentType = "text/csv";
        return context.Response.WriteAsync($"Name,Value\n{response.Name},{response.Value}");
    }
}

#endregion

#region sample_conneg_strict
/// <summary>
/// Strict content negotiation — returns 406 when Accept header doesn't match
/// </summary>
[StrictConneg]
public static class StrictConnegEndpoints
{
    [WolverineGet("/conneg/strict")]
    public static ConnegItem GetStrictItem()
    {
        return new ConnegItem("StrictWidget", 99);
    }

    [Writes("text/plain")]
    public static Task WriteResponse(HttpContext context, ConnegItem response)
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync($"{response.Name}: {response.Value}");
    }
}

#endregion

#region sample_conneg_loose_fallback
/// <summary>
/// Loose content negotiation (default) — falls back to JSON when no match
/// </summary>
public static class LooseConnegEndpoints
{
    [WolverineGet("/conneg/loose")]
    public static ConnegItem GetLooseItem()
    {
        return new ConnegItem("LooseWidget", 77);
    }

    [Writes("text/plain")]
    public static Task WriteResponse(HttpContext context, ConnegItem response)
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync($"{response.Name}: {response.Value}");
    }
}

#endregion
