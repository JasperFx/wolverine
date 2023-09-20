using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Runtime;

namespace Wolverine.Http.Runtime;

public static class TenantIdDetection
{
    public const string NoMandatoryTenantIdCouldBeDetectedForThisHttpRequest = "No mandatory tenant id could be detected for this HTTP request";

    public static string? TryReadFromRoute(HttpContext httpContext, MessageContext messageContext, string argumentName)
    {
        if (messageContext.TenantId.IsEmpty() && httpContext.Request.RouteValues.TryGetValue(argumentName, out var value))
        {
            messageContext.TenantId = value?.ToString();
        }

        return messageContext.TenantId;
    }

    public static string? TryReadFromQueryString(HttpContext httpContext, MessageContext messageContext, string key)
    {
        if (messageContext.TenantId.IsEmpty() && httpContext.Request.Query.TryGetValue(key, out var value))
        {
            messageContext.TenantId = value;
        }
        
        return messageContext.TenantId;
    }
    
    public static string? TryReadFromRequestHeader(HttpContext httpContext, MessageContext messageContext, string headerName)
    {
        if (messageContext.TenantId.IsEmpty() && httpContext.Request.Headers.TryGetValue(headerName, out var value))
        {
            messageContext.TenantId = value;
        }
        
        return messageContext.TenantId;
    }
    
    public static string? TryReadFromClaimsPrincipal(HttpContext httpContext, MessageContext messageContext, string claimType)
    {
        if (messageContext.TenantId.IsEmpty())
        {
            var principal = httpContext.User;
            var claim = principal.Claims.FirstOrDefault(x => x.Type == claimType);
            if (claim != null)
            {
                messageContext.TenantId = claim.Value;
            }
        }
        
        return messageContext.TenantId;
    }

    public static ProblemDetails AssertTenantIdExists(MessageContext messageContext)
    {
        if (messageContext.TenantId.IsEmpty())
        {
            return new ProblemDetails
            {
                Status = 400,
                Detail = NoMandatoryTenantIdCouldBeDetectedForThisHttpRequest
            };
        }

        return WolverineContinue.NoProblems;
    }
    
}