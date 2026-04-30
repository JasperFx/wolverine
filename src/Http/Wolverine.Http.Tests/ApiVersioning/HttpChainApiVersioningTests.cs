using Asp.Versioning;
using Shouldly;
using Wolverine.Http;

namespace Wolverine.Http.Tests.ApiVersioning;

internal class SampleApiVersioningEndpoint
{
    [WolverineGet("/api-versioning/sample")]
    public string Get() => "ok";
}

public class HttpChainApiVersioningTests
{
    private static HttpChain BuildChain()
        => HttpChain.ChainFor<SampleApiVersioningEndpoint>(x => x.Get());

    [Fact]
    public void chain_with_no_version_has_null_api_version()
    {
        var chain = BuildChain();
        chain.ApiVersion.ShouldBeNull();
    }

    [Fact]
    public void has_api_version_sets_property_and_returns_chain()
    {
        var chain = BuildChain();
        var version = new ApiVersion(1, 0);

        var returned = chain.HasApiVersion(version);

        returned.ShouldBeSameAs(chain);
        chain.ApiVersion.ShouldBe(version);
    }

    [Fact]
    public void sunset_policy_round_trips()
    {
        var chain = BuildChain();
        var policy = new SunsetPolicy(DateTimeOffset.UtcNow.AddDays(30));

        chain.SunsetPolicy = policy;

        chain.SunsetPolicy.ShouldBeSameAs(policy);
    }
}
