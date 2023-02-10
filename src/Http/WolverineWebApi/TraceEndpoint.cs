using Microsoft.AspNetCore.Mvc;

namespace WolverineWebApi;

public class TraceEndpoint
{
    [HttpGet("/trace")]
    public string Hey() => "hey";
}