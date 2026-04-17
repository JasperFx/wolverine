using System.Reflection;
using System.Security.Claims;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Http;
using IServiceContainer = JasperFx.IServiceContainer;

namespace WolverineWebApi;

#region sample_fromvaluesource_attribute
/// <summary>
/// Simple test attribute that resolves a parameter value from the configured ValueSource.
/// Used for testing the various value source resolution mechanisms.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class FromValueSourceAttribute : WolverineParameterAttribute
{
    public FromValueSourceAttribute()
    {
    }

    public FromValueSourceAttribute(string argumentName) : base(argumentName)
    {
    }

    public override Variable Modify(IChain chain, ParameterInfo parameter,
        IServiceContainer container, GenerationRules rules)
    {
        if (chain.TryFindVariable(ArgumentName ?? parameter.Name!, ValueSource, parameter.ParameterType, out var variable))
        {
            return variable;
        }

        throw new InvalidOperationException(
            $"Could not resolve value for parameter '{parameter.Name}' using ValueSource.{ValueSource} with argument name '{ArgumentName}'");
    }
}

#endregion

#region sample_value_source_test_endpoints
public static class ValueSourceFromHeaderEndpoint
{
    [WolverineGet("/test/from-header/string")]
    public static string GetStringHeader(
        [FromValueSource(FromHeader = "X-Custom-Value")] string value)
    {
        return value ?? "no-value";
    }

    [WolverineGet("/test/from-header/int")]
    public static string GetIntHeader(
        [FromValueSource(FromHeader = "X-Count")] int count)
    {
        return $"count:{count}";
    }

    [WolverineGet("/test/from-header/guid")]
    public static string GetGuidHeader(
        [FromValueSource(FromHeader = "X-Correlation-Id")] Guid correlationId)
    {
        return $"id:{correlationId}";
    }
}

public static class ValueSourceFromClaimEndpoint
{
    [WolverineGet("/test/from-claim/string")]
    public static string GetStringClaim(
        [FromValueSource(FromClaim = "sub")] string userId)
    {
        return userId ?? "no-user";
    }

    [WolverineGet("/test/from-claim/int")]
    public static string GetIntClaim(
        [FromValueSource(FromClaim = "tenant-id")] int tenantId)
    {
        return $"tenant:{tenantId}";
    }

    [WolverineGet("/test/from-claim/guid")]
    public static string GetGuidClaim(
        [FromValueSource(FromClaim = "organization-id")] Guid orgId)
    {
        return $"org:{orgId}";
    }
}

public static class ValueSourceFromMethodEndpoint
{
    public static Guid ResolveId(ClaimsPrincipal user)
    {
        var claim = user.FindFirstValue("computed-id");
        return claim != null ? Guid.Parse(claim) : Guid.Empty;
    }

    [WolverineGet("/test/from-method/guid")]
    public static string GetMethodValue(
        [FromValueSource(FromMethod = "ResolveId")] Guid resolvedId)
    {
        return $"resolved:{resolvedId}";
    }

    public static string ComputeName(ClaimsPrincipal user)
    {
        return user.FindFirstValue("display-name") ?? "anonymous";
    }

    [WolverineGet("/test/from-method/string")]
    public static string GetMethodString(
        [FromValueSource(FromMethod = "ComputeName")] string name)
    {
        return $"name:{name}";
    }
}

#endregion
