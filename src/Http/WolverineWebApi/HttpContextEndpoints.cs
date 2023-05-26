using System.Security.Claims;
using Shouldly;
using Wolverine.Http;

namespace WolverineWebApi;

public class HttpContextEndpoints
{
    public static ClaimsPrincipal User { get; set; }

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

    #region sample_using_trace_identifier

    [WolverineGet("/http/identifier")]
    public string UseTraceIdentifier(string traceIdentifier)
    {
        return traceIdentifier;
    }

    #endregion
}