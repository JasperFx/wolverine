using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Wolverine.Http;

namespace WolverineWebApi;

public class HttpContextEndpoints
{
    [WolverineGet("/http/context")]
    public void UseHttpContext(HttpContext context)
    {
        context.ShouldNotBeNull();
    }

    [WolverineGet("/http/request")]
    public void UseHttpRequest(HttpRequest request)
    {
        request.ShouldNotBeNull();
    }
    
    [WolverineGet("/http/response")]
    public void UseHttpResponse(HttpResponse response)
    {
        response.ShouldNotBeNull();
    }

    [WolverineGet("/http/principal")]
    public void UseClaimsPrincipal(ClaimsPrincipal user)
    {
        User = user;
    }

    public static ClaimsPrincipal User { get; set; }

    [WolverineGet("/http/identifier")]
    public string UseTraceIdentifier(string traceIdentifier)
    {
        return traceIdentifier;
    }
}