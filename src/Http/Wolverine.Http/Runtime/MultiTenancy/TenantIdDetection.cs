using JasperFx.CodeGeneration;
using Lamar;

namespace Wolverine.Http.Runtime.MultiTenancy;

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

    public void DetectWith(ITenantDetection detection)
    {
        Strategies.Add(detection);
    }

    public void DefaultIs(string defaultTenantId)
    {
        Strategies.Add(new FallbackDefault(defaultTenantId));
    }

    internal IContainer? Container { get; set; }

    public void DetectWith<T>() where T : ITenantDetection
    {
        if (Container == null) throw new InvalidOperationException("The Container has not been set yet");

        DetectWith(Container.GetInstance<T>());
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
        if (Strategies.Count == 0) return;

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