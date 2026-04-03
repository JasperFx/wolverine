using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using WolverineWebApi.Antiforgery;

namespace Wolverine.Http.Tests.Antiforgery;

public class AntiforgeryMetadataTests
{
    [Fact]
    public void form_endpoint_automatically_gets_antiforgery_metadata()
    {
        var chain = HttpChain.ChainFor<AntiforgeryTestEndpoints>(x => x.PostForm(""));
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        var antiforgeryMeta = endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>();
        antiforgeryMeta.ShouldNotBeNull();
        antiforgeryMeta.RequiresValidation.ShouldBeTrue();
    }

    [Fact]
    public void non_form_endpoint_does_not_get_antiforgery_metadata()
    {
        var chain = HttpChain.ChainFor<AntiforgeryTestEndpoints>(x => x.PostJson(null!));
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        var antiforgeryMeta = endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>();
        antiforgeryMeta.ShouldBeNull();
    }

    [Fact]
    public void disable_antiforgery_on_form_endpoint_suppresses_metadata()
    {
        var chain = HttpChain.ChainFor<AntiforgeryTestEndpoints>(x => x.PostFormDisabled(""));
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        var antiforgeryMeta = endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>();
        antiforgeryMeta.ShouldNotBeNull();
        antiforgeryMeta.RequiresValidation.ShouldBeFalse();
    }

    [Fact]
    public void validate_antiforgery_on_non_form_endpoint_adds_metadata()
    {
        var chain = HttpChain.ChainFor<AntiforgeryTestEndpoints>(x => x.PostJsonRequired(null!));
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        var antiforgeryMeta = endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>();
        antiforgeryMeta.ShouldNotBeNull();
        antiforgeryMeta.RequiresValidation.ShouldBeTrue();
    }

    [Fact]
    public void disable_antiforgery_on_class_applies_to_form_endpoints()
    {
        var chain = HttpChain.ChainFor<DisabledAntiforgeryEndpoints>(x => x.PostForm(""));
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        var antiforgeryMeta = endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>();
        antiforgeryMeta.ShouldNotBeNull();
        antiforgeryMeta.RequiresValidation.ShouldBeFalse();
    }
}
