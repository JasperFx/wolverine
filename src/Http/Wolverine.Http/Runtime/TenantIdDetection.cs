using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Lamar;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Http.Runtime;


public interface ITenantDetection
{
    public ValueTask<string?> DetectTenant(HttpContext context);
}

internal class ArgumentDetection : ITenantDetection
{
    private readonly string _argumentName;

    public ArgumentDetection(string argumentName)
    {
        _argumentName = argumentName;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext)
    {
        return httpContext.Request.RouteValues.TryGetValue(_argumentName, out var value) 
            ? new ValueTask<string?>(value?.ToString()) 
            : ValueTask.FromResult<string?>(null);
    }

    public override string ToString()
    {
        return $"Tenant Id is route argument named '{_argumentName}'";
    }
}

internal class QueryStringDetection : ITenantDetection
{
    private readonly string _key;

    public QueryStringDetection(string key)
    {
        _key = key;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext)
    {
        return httpContext.Request.Query.TryGetValue(_key, out var value) 
            ? ValueTask.FromResult<string?>(value) 
            : ValueTask.FromResult<string?>(null);
    }

    public override string ToString()
    {
        return $"Tenant Id is query string value '{_key}'";
    }
}


internal class RequestHeaderDetection : ITenantDetection
{
    private readonly string _headerName;

    public RequestHeaderDetection(string headerName)
    {
        _headerName = headerName;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext)
    {
        return httpContext.Request.Headers.TryGetValue(_headerName, out var value)
            ? ValueTask.FromResult<string?>(value)
            : ValueTask.FromResult<string?>(null);
    }

    public override string ToString()
    {
        return $"Tenant Id is request header '{_headerName}'";
    }
}

internal class ClaimsPrincipalDetection : ITenantDetection
{
    private readonly string _claimType;

    public ClaimsPrincipalDetection(string claimType)
    {
        _claimType = claimType;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext)
    {
        var principal = httpContext.User;
        var claim = principal.Claims.FirstOrDefault(x => x.Type == _claimType);

        return claim == null ? ValueTask.FromResult<string?>(null) : ValueTask.FromResult<string?>(claim.Value);
    }

    public override string ToString()
    {
        return $"Tenant Id is value of claim '{_claimType}'";
    }
}

internal class SubDomainNameDetection : ITenantDetection
{
    public ValueTask<string?> DetectTenant(HttpContext httpContext)
    {
        var parts = httpContext.Request.Host.Host.Split('.');
        if (parts.Length > 1)
        {
            return ValueTask.FromResult<string?>(parts[0]);
        }

        return ValueTask.FromResult<string?>(null);
    }

    public override string ToString()
    {
        return "Tenant Id is first sub domain name of the request host";
    }
}


internal class TenantIdDetection : ITenantDetectionPolicies, IHttpPolicy
{
    public const string NoMandatoryTenantIdCouldBeDetectedForThisHttpRequest = "No mandatory tenant id could be detected for this HTTP request";

    public List<ITenantDetection> Strategies { get; } = new();

    public void IsRouteArgumentNamed(string routeArgumentName)
    {
        Strategies.Add(new ArgumentDetection(routeArgumentName));
    }

    public void IsQueryStringValue(string key)
    {
        Strategies.Add(new QueryStringDetection(key));
    }

    public void IsRequestHeaderValue(string headerKey)
    {
        Strategies.Add(new RequestHeaderDetection(headerKey));
    }

    public void IsClaimTypeNamed(string claimType)
    {
        Strategies.Add(new ClaimsPrincipalDetection(claimType));
    }

    public void IsSubDomainName()
    {
        Strategies.Add(new SubDomainNameDetection());
    }

    public void AssertExists()
    {
        AssertTenantExists = true;
    }

    public bool AssertTenantExists { get; set; }


    public bool ShouldAssertTenantIdExists(HttpChain chain)
    {
        if (!AssertTenantExists) return false;

        if (chain.TenancyMode == null) return true;

        return chain.TenancyMode == TenancyMode.Required;
    }

    void IHttpPolicy.Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container)
    {
        if (!Strategies.Any()) return;

        foreach (var chain in chains)
        {
            if (CanBeTenanted(chain))
            {
                chain.Middleware.Insert(0, new DetectTenantIdFrame(this, chain));
            }
        }
    }

    internal bool CanBeTenanted(HttpChain chain)
    {
        if (chain.TenancyMode == null) return true;

        if (chain.TenancyMode == TenancyMode.None) return false;

        return true;
    }
}

internal class DetectTenantIdFrame : AsyncFrame
{
    private readonly TenantIdDetection _options;
    private readonly HttpChain _chain;
    private Variable _httpContext;
    private Variable _messageContext;

    public DetectTenantIdFrame(TenantIdDetection options, HttpChain chain)
    {
        _options = options;
        _chain = chain;

        TenantId = new Variable( typeof(string), PersistenceConstants.TenantIdVariableName, this);
    }

    public Variable TenantId { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;

        _messageContext = chain.FindVariable(typeof(MessageContext));
        yield return _messageContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Tenant Id detection");
        for (int i = 0; i < _options.Strategies.Count ; i++)
        {
            writer.WriteComment($"{i + 1}. {_options.Strategies[i]}");
        }
        
        writer.Write($"var {TenantId.Usage} = await {nameof(HttpHandler.TryDetectTenantId)}({_httpContext.Usage});");
        writer.Write($"{_messageContext.Usage}.{nameof(MessageContext.TenantId)} = {TenantId.Usage};");

        if (_options.ShouldAssertTenantIdExists(_chain))
        {
            writer.Write($"BLOCK:if (string.{nameof(string.IsNullOrEmpty)}({TenantId.Usage}))");
            writer.Write($"await {nameof(HttpHandler.WriteTenantIdNotFound)}({_httpContext.Usage});");
            writer.Write("return;");
            writer.FinishBlock();
        }
        
        Next?.GenerateCode(method, writer);
    }
}

