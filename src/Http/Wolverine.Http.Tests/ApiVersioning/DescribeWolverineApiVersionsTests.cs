using Asp.Versioning;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.IoC;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

// ---------------------------------------------------------------------------
// Handler fixtures — dedicated to these tests to avoid fixture name collisions
// ---------------------------------------------------------------------------

internal class DescribeV1Handler
{
    [WolverineGet("/describe-v1")]
    [ApiVersion("1.0")]
    public string Get() => "v1";
}

internal class DescribeV2HandlerA
{
    [WolverineGet("/describe-v2-a")]
    [ApiVersion("2.0")]
    public string Get() => "v2a";
}

internal class DescribeV2HandlerB
{
    [WolverineGet("/describe-v2-b")]
    [ApiVersion("2.0")]
    public string Get() => "v2b";
}

internal class DescribeV3Handler
{
    [WolverineGet("/describe-v3")]
    [ApiVersion("3.0")]
    public string Get() => "v3";
}

internal class DescribeDeprecatedV1Handler
{
    [WolverineGet("/describe-deprecated-v1")]
    [ApiVersion("1.0", Deprecated = true)]
    public string Get() => "deprecated-v1";
}

internal class DescribeUnversionedHandler
{
    [WolverineGet("/describe-unversioned")]
    public string Get() => "unversioned";
}

// ---------------------------------------------------------------------------
// Test helpers
// ---------------------------------------------------------------------------

/// <summary>
/// Constructs an <see cref="IEndpointRouteBuilder"/> stub backed by a real
/// <see cref="WolverineHttpOptions"/> with the supplied chains pre-installed.
/// </summary>
internal static class DescribeTestHelper
{
    /// <summary>
    /// Builds a minimal service provider, populates a <see cref="HttpGraph"/> with
    /// the given <paramref name="chains"/> (which must already have ApiVersion etc.
    /// set via a policy), wires it into <see cref="WolverineHttpOptions.Endpoints"/>,
    /// and returns a stub <see cref="IEndpointRouteBuilder"/> whose
    /// <c>ServiceProvider</c> resolves that options instance.
    /// </summary>
    public static IEndpointRouteBuilder BuildEndpoints(
        WolverineHttpOptions httpOptions,
        IReadOnlyList<HttpChain> chains)
    {
        // Build a minimal IServiceContainer so HttpGraph can be constructed.
        var registry = new ServiceCollection();
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IServiceCollection>(registry);
        var sp = registry.BuildServiceProvider();
        var container = sp.GetRequiredService<IServiceContainer>();

        var graph = new HttpGraph(new WolverineOptions(), container);

        // Inject the pre-built chains into the graph.
        // HttpGraph.Chains is public read-only, but _chains (the backing field) is private.
        // Direct field access is necessary to preserve pre-built chains with their applied policies.
        InjectChainsIntoGraph(graph, chains);

        httpOptions.Endpoints = graph;

        // Build a fake IServiceProvider that returns our WolverineHttpOptions.
        var fakeServiceProvider = Substitute.For<IServiceProvider>();
        fakeServiceProvider.GetService(typeof(WolverineHttpOptions)).Returns(httpOptions);

        var fakeEndpoints = Substitute.For<IEndpointRouteBuilder>();
        fakeEndpoints.ServiceProvider.Returns(fakeServiceProvider);

        return fakeEndpoints;
    }

    /// <summary>
    /// Injects pre-built HttpChain instances into an HttpGraph's internal _chains collection.
    /// This preserves chains with their applied policies (e.g., ApiVersioning metadata).
    /// HttpGraph.Chains is public read-only, so direct field access via reflection is required.
    /// </summary>
    private static void InjectChainsIntoGraph(HttpGraph graph, IReadOnlyList<HttpChain> chains)
    {
        var chainsField = typeof(HttpGraph)
            .GetField("_chains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var list = (List<HttpChain>)chainsField.GetValue(graph)!;
        foreach (var chain in chains)
            list.Add(chain);
    }

    /// <summary>
    /// Convenience overload: applies the versioning policy and builds the stubs in one step.
    /// </summary>
    public static IEndpointRouteBuilder BuildEndpointsWithPolicy(
        WolverineHttpOptions httpOptions,
        params HttpChain[] chains)
    {
        if (httpOptions.ApiVersioning is not null)
        {
            var policy = new ApiVersioningPolicy(httpOptions.ApiVersioning);
            policy.Apply(chains, new GenerationRules(), null!);
        }

        return BuildEndpoints(httpOptions, chains);
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class DescribeWolverineApiVersionsTests
{
    // 1 — returns empty list when versioning is not configured
    [Fact]
    public void returns_empty_list_when_versioning_not_configured()
    {
        var httpOptions = new WolverineHttpOptions();
        // No UseApiVersioning call — ApiVersioning is null.

        var chain = HttpChain.ChainFor<DescribeV1Handler>(x => x.Get());
        var endpoints = DescribeTestHelper.BuildEndpoints(httpOptions, new[] { chain });

        var result = endpoints.DescribeWolverineApiVersions();

        result.ShouldBeEmpty();
    }

    // 2 — returns one description per distinct version
    [Fact]
    public void returns_one_description_per_distinct_version()
    {
        var httpOptions = new WolverineHttpOptions();
        httpOptions.UseApiVersioning(_ => { });

        var v1Chain = HttpChain.ChainFor<DescribeV1Handler>(x => x.Get());
        var v2aChain = HttpChain.ChainFor<DescribeV2HandlerA>(x => x.Get());
        var v2bChain = HttpChain.ChainFor<DescribeV2HandlerB>(x => x.Get());

        var endpoints = DescribeTestHelper.BuildEndpointsWithPolicy(
            httpOptions, v1Chain, v2aChain, v2bChain);

        var result = endpoints.DescribeWolverineApiVersions();

        result.Count.ShouldBe(2);
        result.Select(d => d.ApiVersion).ShouldBe(
            new[] { new ApiVersion(1, 0), new ApiVersion(2, 0) },
            ignoreOrder: false);
    }

    // 3 — results are sorted ascending by version
    [Fact]
    public void descriptions_sorted_ascending_by_version()
    {
        var httpOptions = new WolverineHttpOptions();
        httpOptions.UseApiVersioning(_ => { });

        var v3Chain = HttpChain.ChainFor<DescribeV3Handler>(x => x.Get());
        var v1Chain = HttpChain.ChainFor<DescribeV1Handler>(x => x.Get());

        // Pass v3 first intentionally.
        var endpoints = DescribeTestHelper.BuildEndpointsWithPolicy(
            httpOptions, v3Chain, v1Chain);

        var result = endpoints.DescribeWolverineApiVersions();

        result.Count.ShouldBe(2);
        result[0].ApiVersion.ShouldBe(new ApiVersion(1, 0));
        result[1].ApiVersion.ShouldBe(new ApiVersion(3, 0));
    }

    // 4 — document name comes from the configured strategy
    [Fact]
    public void description_has_correct_document_name_from_strategy()
    {
        var httpOptions = new WolverineHttpOptions();
        httpOptions.UseApiVersioning(opts =>
            opts.OpenApi.DocumentNameStrategy = v => "v" + v.MajorVersion);

        var v1Chain = HttpChain.ChainFor<DescribeV1Handler>(x => x.Get());

        var endpoints = DescribeTestHelper.BuildEndpointsWithPolicy(httpOptions, v1Chain);

        var result = endpoints.DescribeWolverineApiVersions();

        result.Count.ShouldBe(1);
        result[0].DocumentName.ShouldBe("v1");
        result[0].DisplayName.ShouldBe("API v1");
    }

    // 5 — IsDeprecated is true when [ApiVersion(Deprecated = true)] is on the handler
    [Fact]
    public void is_deprecated_true_when_attribute_marks_handler()
    {
        var httpOptions = new WolverineHttpOptions();
        httpOptions.UseApiVersioning(_ => { });

        var chain = HttpChain.ChainFor<DescribeDeprecatedV1Handler>(x => x.Get());

        var endpoints = DescribeTestHelper.BuildEndpointsWithPolicy(httpOptions, chain);

        var result = endpoints.DescribeWolverineApiVersions();

        result.Count.ShouldBe(1);
        result[0].IsDeprecated.ShouldBeTrue();
    }

    // 6 — IsDeprecated is true when options.Deprecate() was called (no attribute)
    [Fact]
    public void is_deprecated_true_when_options_deprecate_called()
    {
        var date = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var httpOptions = new WolverineHttpOptions();
        httpOptions.UseApiVersioning(opts => opts.Deprecate("2.0").On(date));

        var v2aChain = HttpChain.ChainFor<DescribeV2HandlerA>(x => x.Get());
        var v2bChain = HttpChain.ChainFor<DescribeV2HandlerB>(x => x.Get());

        var endpoints = DescribeTestHelper.BuildEndpointsWithPolicy(
            httpOptions, v2aChain, v2bChain);

        var result = endpoints.DescribeWolverineApiVersions();

        result.Count.ShouldBe(1);
        result[0].ApiVersion.ShouldBe(new ApiVersion(2, 0));
        result[0].IsDeprecated.ShouldBeTrue();
    }

    // 7 — SunsetPolicy is present when opts.Sunset() is configured
    [Fact]
    public void sunset_policy_present_when_configured()
    {
        var date = new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var httpOptions = new WolverineHttpOptions();
        httpOptions.UseApiVersioning(opts => opts.Sunset("1.0").On(date));

        var v1Chain = HttpChain.ChainFor<DescribeV1Handler>(x => x.Get());

        var endpoints = DescribeTestHelper.BuildEndpointsWithPolicy(httpOptions, v1Chain);

        var result = endpoints.DescribeWolverineApiVersions();

        result.Count.ShouldBe(1);
        result[0].SunsetPolicy.ShouldNotBeNull();
        result[0].SunsetPolicy!.Date.ShouldBe(date);
    }
}
