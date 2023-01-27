using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace WolverineWebApi;

public class HttpContextEndpoints
{
    [HttpGet("/http/context")]
    public void UseHttpContext(HttpContext context)
    {
        context.ShouldNotBeNull();
    }

    [HttpGet("/http/request")]
    public void UseHttpRequest(HttpRequest request)
    {
        request.ShouldNotBeNull();
    }
    
    [HttpGet("/http/response")]
    public void UseHttpResponse(HttpResponse response)
    {
        response.ShouldNotBeNull();
    }

    [HttpGet("/http/principal")]
    public void UseClaimsPrincipal(ClaimsPrincipal user)
    {
        User = user;
    }

    public static ClaimsPrincipal User { get; set; }

    [HttpGet("/http/identifier")]
    public string UseTraceIdentifier(string traceIdentifier)
    {
        return traceIdentifier;
    }
}