using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

public class ResultEndpoints
{
    [WolverineGet("/result")]
    public static IResult GetResult()
    {
        return Microsoft.AspNetCore.Http.Results.Content("Hello from result", "text/plain");
    }
    
    [WolverineGet("/result-async")]
    public static Task<IResult> GetAsyncResult()
    {
        var result = Microsoft.AspNetCore.Http.Results.Content("Hello from async result", "text/plain");
        return Task.FromResult(result);
    }
}

