using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

public class HeaderUsingEndpoint
{
    // For testing
    public static string Day;


    #region sample_pushing_header_values_into_endpoint_methods

    // As of Wolverine 2.6, you can utilize header data in middleware
    public static void Before([FromHeader(Name = "x-day")] string? day)
    {
        Debug.WriteLine($"Day header is {day}");
        Day = day; // This is for testing
    }

    [WolverineGet("/headers/simple")]
    public string Get(
        // Find the request header with the supplied name and pass
        // it as the "name" parameter to this method at runtime
        [FromHeader(Name = "x-wolverine")]
        string name)
    {
        return name;
    }

    [WolverineGet("/headers/int")]
    public string Get(
        // Find the request header with the supplied name and pass
        // it as the "name" parameter to this method at runtime
        // If the attribute does not exist, Wolverine will pass
        // in the default value for the parameter type, in this case
        // 0
        [FromHeader(Name = "x-wolverine")] int number
    )
    {
        return (number * 2).ToString();
    }

    [WolverineGet("/headers/accepts")]
    // In this case, push the string value for the "accepts" header
    // right into the parameter based on the parameter name
    public string GetETag([FromHeader] string accepts)
    {
        return accepts;
    }

    #endregion
}


public class HeaderMiddlewareAttribute : ModifyHttpChainAttribute
{
    public override void Modify(HttpChain chain, GenerationRules rules)
    {
        chain.Middleware.Add(MethodCall.For<HeaderMiddleware>(x => x.Before(default)));
    }
}

public class HeaderMiddleware
{
    public record MiddlewareResult(string Value);

    public MiddlewareResult Before([FromHeader(Name = "x-middleware")] string valueMiddleware)
    {
        return new MiddlewareResult(valueMiddleware);
    }
}

public class MiddlewareEndpoint
{
    public record BeforeResult(string Value);

    public record Result(string Handler, string Before, string Middleware);

    public BeforeResult Before([FromHeader(Name = "x-before")] string valueBefore)
    {
        return new BeforeResult(valueBefore);
    }

    [WolverineGet("/middleware/header")]
    [HeaderMiddleware]
    public Result HandleGet([FromHeader(Name = "x-handler")] string valueEndpoint, BeforeResult before, HeaderMiddleware.MiddlewareResult middleware)
    {
        return new(valueEndpoint, before.Value, middleware.Value);
    }
}