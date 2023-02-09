using Microsoft.AspNetCore.Mvc;

namespace WolverineWebApi;

public class ResultEndpoints
{
    [HttpGet("/result")]
    public static IResult GetResult()
    {
        return Microsoft.AspNetCore.Http.Results.Content("Hello from result", "text/plain");
    }
    
    [HttpGet("/result/async")]
    public static Task<IResult> GetAsyncResult()
    {
        var result = Microsoft.AspNetCore.Http.Results.Content("Hello from async result", "text/plain");
        return Task.FromResult(result);
    }
}