using Asp.Versioning;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

// ---------- Test handler fixtures ----------

[ApiVersionNeutral]
internal class HealthHandler
{
    [WolverineGet("/health")]
    public string Get() => "ok";
}

// Second neutral handler at a *different* route — exercises the
// "two neutral chains at distinct routes do not collide" path. The handler-method
// name `Get` deliberately mirrors HealthHandler.Get so we also exercise the
// EndpointName / OperationId disambiguation.
[ApiVersionNeutral]
internal class StatusHandler
{
    [WolverineGet("/status")]
    public string Get() => "ok";
}

// Second neutral handler at the *same* route as HealthHandler — exercises the
// "two neutral chains at the same (verb, route) throw" path.
[ApiVersionNeutral]
internal class DuplicateHealthHandler
{
    [WolverineGet("/health")]
    public string Get() => "dup";
}

[ApiVersion("1.0")]
internal class MixedVersionedHandler
{
    [WolverineGet("/customers")]
    public string GetCustomers() => "v1-customers";

    [ApiVersionNeutral]
    [WolverineGet("/customers/ping")]
    public string Ping() => "pong";
}

// Class-level [ApiVersionNeutral] but with a method that explicitly opts back
// in via [ApiVersion]. Per the documented rule "method wins", this method must
// be treated as versioned, NOT as neutral.
[ApiVersionNeutral]
internal class MixedNeutralHandler
{
    [WolverineGet("/inventory/stock")]
    public string Stock() => "neutral-stock";

    [ApiVersion("2.0")]
    [WolverineGet("/inventory/orders")]
    public string Orders() => "v2-orders";
}

[ApiVersion("1.0")]
[ApiVersionNeutral]
internal class ConflictingClassHandler
{
    [WolverineGet("/conflict-class")]
    public string Get() => "conflict";
}

internal class ConflictingMethodHandler
{
    [ApiVersion("1.0")]
    [ApiVersionNeutral]
    [WolverineGet("/conflict-method")]
    public string Get() => "conflict";
}

[ApiVersion("1.0")]
internal class HealthVersionedHandler
{
    [WolverineGet("/health")]
    public string Get() => "v1-health";
}

// Used to verify the resolver clears any earlier fluent HasApiVersion(...) assignment
// when a method-level [ApiVersionNeutral] is present.
internal class FluentlyVersionedNeutralHandler
{
    [ApiVersionNeutral]
    [WolverineGet("/legacy/ping")]
    public string Ping() => "pong";
}

// ---------- Tests ----------

public class ApiVersionNeutralTests
{
    // Mirror HttpChain.ChainFor: build a real IServiceContainer rather than passing null.
    // Passing null! works while ApiVersioningPolicy never touches the container, but as soon
    // as any future step (or a misordered call) does, null! produces an opaque NRE far from
    // the call site. A real container is a few lines of code and zero risk.
    private static IServiceContainer NewContainer()
    {
        var registry = new ServiceCollection();
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IServiceCollection>(registry);
        return registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();
    }

    private static void Apply(ApiVersioningPolicy policy, params HttpChain[] chains)
        => policy.Apply(chains, new GenerationRules(), NewContainer());

    // 1 — class-level neutrality: no rewrite, no version, ApiVersion stays null
    [Fact]
    public void class_level_neutral_does_not_rewrite_route()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<HealthHandler>(x => x.Get());
        var originalRoute = chain.RoutePattern!.RawText;

        Apply(policy, chain);

        chain.IsApiVersionNeutral.ShouldBeTrue();
        chain.ApiVersion.ShouldBeNull();
        chain.RoutePattern!.RawText.ShouldBe(originalRoute);
    }

    // 1b — class-level neutrality: ApiVersionMetadata.IsApiVersionNeutral is true
    [Fact]
    public void class_level_neutral_attaches_neutral_apiversion_metadata()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<HealthHandler>(x => x.Get());

        Apply(policy, chain);

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
        var versionMeta = endpoint.Metadata.GetMetadata<ApiVersionMetadata>();
        versionMeta.ShouldNotBeNull();
        versionMeta!.IsApiVersionNeutral.ShouldBeTrue();
    }

    // 1c — neutral chains do not get a group name (so they fall into the default OpenAPI doc).
    [Fact]
    public void class_level_neutral_attaches_no_group_name()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<HealthHandler>(x => x.Get());

        Apply(policy, chain);

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
        endpoint.Metadata.GetMetadata<IEndpointGroupNameMetadata>().ShouldBeNull();
    }

    // 1d — neutral chains do not get sunset/deprecation/api-supported-versions header writers.
    [Fact]
    public void class_level_neutral_does_not_get_header_state_metadata()
    {
        var opts = new WolverineApiVersioningOptions { EmitApiSupportedVersionsHeader = true };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<HealthHandler>(x => x.Get());

        Apply(policy, chain);

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
        endpoint.Metadata.GetMetadata<ApiVersionEndpointHeaderState>().ShouldBeNull();
    }

    // 2 — method-level neutrality inside an [ApiVersion] class only affects that one method
    [Fact]
    public void method_level_neutral_inside_versioned_class_only_affects_that_method()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);

        var pingChain = HttpChain.ChainFor<MixedVersionedHandler>(x => x.Ping());
        var customersChain = HttpChain.ChainFor<MixedVersionedHandler>(x => x.GetCustomers());

        Apply(policy, pingChain, customersChain);

        pingChain.IsApiVersionNeutral.ShouldBeTrue();
        pingChain.ApiVersion.ShouldBeNull();
        pingChain.RoutePattern!.RawText.ShouldBe("/customers/ping");

        customersChain.IsApiVersionNeutral.ShouldBeFalse();
        customersChain.ApiVersion.ShouldBe(new ApiVersion(1, 0));
        customersChain.RoutePattern!.RawText.ShouldBe("/v1/customers");
    }

    // 3 — neutral chain satisfies RequireExplicit
    [Fact]
    public void neutral_chain_satisfies_require_explicit_policy()
    {
        var opts = new WolverineApiVersioningOptions { UnversionedPolicy = UnversionedPolicy.RequireExplicit };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<HealthHandler>(x => x.Get());

        Should.NotThrow(() => Apply(policy, chain));
    }

    // 4 — class-level conflict: [ApiVersion] + [ApiVersionNeutral] on same class throws
    //     and the message names the offending type AND both attribute names.
    [Fact]
    public void conflicting_attributes_on_class_throws_and_names_target_and_attributes()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<ConflictingClassHandler>(x => x.Get());

        var ex = Should.Throw<InvalidOperationException>(() => Apply(policy, chain));
        ex.Message.ShouldContain(nameof(ConflictingClassHandler));
        ex.Message.ShouldContain("[ApiVersion]");
        ex.Message.ShouldContain("[ApiVersionNeutral]");
    }

    // 4b — method-level conflict: message names handler type, method name, and both attributes.
    [Fact]
    public void conflicting_attributes_on_method_throws_and_names_handler_method_and_attributes()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<ConflictingMethodHandler>(x => x.Get());

        var ex = Should.Throw<InvalidOperationException>(() => Apply(policy, chain));
        ex.Message.ShouldContain(nameof(ConflictingMethodHandler));
        ex.Message.ShouldContain(nameof(ConflictingMethodHandler.Get));
        ex.Message.ShouldContain("[ApiVersion]");
        ex.Message.ShouldContain("[ApiVersionNeutral]");
    }

    // 5 — neutral chain skipped from duplicate-detection on the version axis: a versioned chain
    // and a neutral chain at the same (verb, route) do not collide.
    [Fact]
    public void neutral_chain_does_not_participate_in_versioned_duplicate_detection()
    {
        var opts = new WolverineApiVersioningOptions { UrlSegmentPrefix = null };
        var policy = new ApiVersioningPolicy(opts);

        var neutralChain = HttpChain.ChainFor<HealthHandler>(x => x.Get());
        var versionedAtSameRoute = HttpChain.ChainFor<HealthVersionedHandler>(x => x.Get());

        Should.NotThrow(() => Apply(policy, neutralChain, versionedAtSameRoute));
    }

    // 6 — two neutral chains at *different* routes do not collide and each chain receives a
    // distinct, OperationId-derived endpoint name (so ASP.NET Core's EndpointDataSource cannot
    // throw a duplicate-name error). This guards the parallel of the versioned-chain
    // OperationId disambiguation.
    [Fact]
    public void two_neutral_chains_at_different_routes_do_not_collide_and_have_distinct_endpoint_names()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);

        var healthChain = HttpChain.ChainFor<HealthHandler>(x => x.Get());
        var statusChain = HttpChain.ChainFor<StatusHandler>(x => x.Get());

        Should.NotThrow(() => Apply(policy, healthChain, statusChain));

        // OperationId is unique per handler-type+method, so explicit endpoint names must differ.
        healthChain.HasExplicitOperationId.ShouldBeTrue();
        statusChain.HasExplicitOperationId.ShouldBeTrue();
        healthChain.OperationId.ShouldNotBe(statusChain.OperationId);

        // The endpoint metadata must surface the disambiguated name to ASP.NET Core.
        var healthEndpoint = healthChain.BuildEndpoint(RouteWarmup.Lazy);
        var statusEndpoint = statusChain.BuildEndpoint(RouteWarmup.Lazy);
        var healthName = healthEndpoint.Metadata.GetMetadata<IEndpointNameMetadata>();
        var statusName = statusEndpoint.Metadata.GetMetadata<IEndpointNameMetadata>();
        healthName.ShouldNotBeNull();
        statusName.ShouldNotBeNull();
        healthName!.EndpointName.ShouldBe(healthChain.OperationId);
        statusName!.EndpointName.ShouldBe(statusChain.OperationId);
        healthName.EndpointName.ShouldNotBe(statusName.EndpointName);
    }

    // 7 — two neutral chains at the *same* (verb, route) throw at startup with a clear message
    // naming both handlers. Without this guard, ASP.NET Core would later throw an opaque
    // routing error at first request time.
    [Fact]
    public void two_neutral_chains_at_same_verb_and_route_throw_with_both_handler_names()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);

        var first = HttpChain.ChainFor<HealthHandler>(x => x.Get());
        var second = HttpChain.ChainFor<DuplicateHealthHandler>(x => x.Get());

        var ex = Should.Throw<InvalidOperationException>(() => Apply(policy, first, second));
        ex.Message.ShouldContain("GET");
        ex.Message.ShouldContain("/health");
        ex.Message.ShouldContain(nameof(HealthHandler));
        ex.Message.ShouldContain(nameof(DuplicateHealthHandler));
    }

    // 8 — direction A of "method wins": class-level [ApiVersion] + method-level [ApiVersionNeutral]
    // → method is neutral. (Already covered by test 2 above; reasserted here to keep both
    // directions visible side by side for the spec.)
    [Fact]
    public void method_level_neutral_overrides_class_level_apiversion()
    {
        ApiVersionNeutralResolver.Resolve(
            typeof(MixedVersionedHandler).GetMethod(nameof(MixedVersionedHandler.Ping))!)
            .ShouldBeTrue();
    }

    // 9 — direction B of "method wins": class-level [ApiVersionNeutral] + method-level [ApiVersion]
    // → method is NOT neutral and resolves to the method-level version. Previously broken:
    // the resolver returned true unconditionally because of class-level neutrality.
    [Fact]
    public void method_level_apiversion_overrides_class_level_neutral()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);

        var stockChain = HttpChain.ChainFor<MixedNeutralHandler>(x => x.Stock());
        var ordersChain = HttpChain.ChainFor<MixedNeutralHandler>(x => x.Orders());

        Apply(policy, stockChain, ordersChain);

        // Stock keeps class-level neutrality.
        stockChain.IsApiVersionNeutral.ShouldBeTrue();
        stockChain.ApiVersion.ShouldBeNull();
        stockChain.RoutePattern!.RawText.ShouldBe("/inventory/stock");

        // Orders carries method-level [ApiVersion("2.0")] which must beat class-level neutral.
        ordersChain.IsApiVersionNeutral.ShouldBeFalse();
        ordersChain.ApiVersion.ShouldBe(new ApiVersion(2, 0));
        ordersChain.RoutePattern!.RawText.ShouldBe("/v2/inventory/orders");
    }

    // 10 — method-level [ApiVersionNeutral] must clear any earlier fluent HasApiVersion(...)
    // assignment before later steps (DetectDuplicateRoutes, RewriteRoutes) observe the chain.
    // Otherwise a rogue version would survive on a chain the user explicitly opted out.
    [Fact]
    public void method_level_neutral_clears_prior_fluent_apiversion_assignment()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<FluentlyVersionedNeutralHandler>(x => x.Ping());
        var originalRoute = chain.RoutePattern!.RawText;

        // Simulate a fluent .HasApiVersion("3.0") assignment that ran before the policy.
        chain.ApiVersion = new ApiVersion(3, 0);

        Apply(policy, chain);

        chain.IsApiVersionNeutral.ShouldBeTrue();
        chain.ApiVersion.ShouldBeNull();
        chain.RoutePattern!.RawText.ShouldBe(originalRoute);   // not rewritten
    }
}
