using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Http.Runtime;

namespace Wolverine.Http.CodeGen;

internal class RouteArgumentTenantDetectionFrame : MethodCall
{
    public RouteArgumentTenantDetectionFrame(string routeArgumentName) : base(typeof(TenantIdDetection), nameof(TenantIdDetection.TryReadFromRoute))
    {
        CommentText =
            $"Try to set the tenant id of the message context based on the route argument '{routeArgumentName}'";
        Arguments[2] = Constant.ForString(routeArgumentName);
    }
}

internal class QueryStringTenantDetectionFrame : MethodCall
{
    public QueryStringTenantDetectionFrame(string key) : base(typeof(TenantIdDetection), nameof(TenantIdDetection.TryReadFromQueryString))
    {
        CommentText = $"Try to set the tenant id of the message context based on the query string value '{key}'";
        Arguments[2] = Constant.ForString(key);
    }
}

internal class RequestHeaderTenantDetectionFrame : MethodCall
{
    public RequestHeaderTenantDetectionFrame(string headerKey) : base(typeof(TenantIdDetection), nameof(TenantIdDetection.TryReadFromRequestHeader))
    {
        CommentText =
            $"Try to set the tenant id of the message context based on the value of request header '{headerKey}'";
        Arguments[2] = Constant.ForString(headerKey);
    }
}

internal class ClaimTypeTenantDetectionFrame : MethodCall
{
    public ClaimTypeTenantDetectionFrame(string claimType) : base(typeof(TenantIdDetection), nameof(TenantIdDetection.TryReadFromClaimsPrincipal))
    {
        CommentText = $"Try to set the tenant id of the message context based on the value of claim '{claimType}'";
        Arguments[2] = Constant.ForString(claimType);
    }
}