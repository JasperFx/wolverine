using Shouldly;
using Wolverine.Http;

namespace Wolverine.Http.Tests;

public class RoutePrefixTests
{
    [Fact]
    public void global_prefix_applied_to_all_endpoints()
    {
        var options = new WolverineHttpOptions();
        options.RoutePrefix("api");

        var chain = HttpChain.ChainFor<RoutePrefixTestEndpoints>(x => x.GetItems());
        chain.RoutePattern!.RawText.ShouldBe("/items");

        var policy = new RoutePrefixPolicy(options);
        var prefix = policy.DeterminePrefix(chain);
        prefix.ShouldBe("api");

        RoutePrefixPolicy.PrependPrefix(chain, prefix!);
        chain.RoutePattern!.RawText.ShouldBe("/api/items");
    }

    [Fact]
    public void namespace_specific_prefix()
    {
        var options = new WolverineHttpOptions();
        options.RoutePrefix("api/v1", forEndpointsInNamespace: "Wolverine.Http.Tests");

        var chain = HttpChain.ChainFor<RoutePrefixTestEndpoints>(x => x.GetItems());

        var policy = new RoutePrefixPolicy(options);
        var prefix = policy.DeterminePrefix(chain);
        prefix.ShouldBe("api/v1");

        RoutePrefixPolicy.PrependPrefix(chain, prefix!);
        chain.RoutePattern!.RawText.ShouldBe("/api/v1/items");
    }

    [Fact]
    public void namespace_prefix_does_not_match_unrelated_namespace()
    {
        var options = new WolverineHttpOptions();
        options.RoutePrefix("api/orders", forEndpointsInNamespace: "SomeOther.Namespace");

        var chain = HttpChain.ChainFor<RoutePrefixTestEndpoints>(x => x.GetItems());

        var policy = new RoutePrefixPolicy(options);
        var prefix = policy.DeterminePrefix(chain);
        // Should fall back to null since no global prefix and namespace doesn't match
        prefix.ShouldBeNull();
    }

    [Fact]
    public void namespace_prefix_with_global_fallback()
    {
        var options = new WolverineHttpOptions();
        options.RoutePrefix("api");
        options.RoutePrefix("api/orders", forEndpointsInNamespace: "SomeOther.Namespace");

        var chain = HttpChain.ChainFor<RoutePrefixTestEndpoints>(x => x.GetItems());

        var policy = new RoutePrefixPolicy(options);
        var prefix = policy.DeterminePrefix(chain);
        // Should fall back to global prefix since namespace doesn't match
        prefix.ShouldBe("api");
    }

    [Fact]
    public void attribute_prefix_on_class_takes_precedence()
    {
        var options = new WolverineHttpOptions();
        options.RoutePrefix("api");
        options.RoutePrefix("api/orders", forEndpointsInNamespace: "Wolverine.Http.Tests");

        var chain = HttpChain.ChainFor<PrefixedEndpoints>(x => x.GetOrders());

        var policy = new RoutePrefixPolicy(options);
        var prefix = policy.DeterminePrefix(chain);
        // Should use the attribute prefix, not the namespace or global one
        prefix.ShouldBe("v2/orders");
    }

    [Fact]
    public void no_prefix_when_none_configured()
    {
        var options = new WolverineHttpOptions();

        var chain = HttpChain.ChainFor<RoutePrefixTestEndpoints>(x => x.GetItems());

        var policy = new RoutePrefixPolicy(options);
        var prefix = policy.DeterminePrefix(chain);
        prefix.ShouldBeNull();
    }

    [Fact]
    public void handles_leading_and_trailing_slashes_in_global_prefix()
    {
        var options = new WolverineHttpOptions();
        options.RoutePrefix("/api/");

        options.GlobalRoutePrefix.ShouldBe("api");
    }

    [Fact]
    public void handles_leading_and_trailing_slashes_in_namespace_prefix()
    {
        var options = new WolverineHttpOptions();
        options.RoutePrefix("/api/orders/", forEndpointsInNamespace: "MyApp");

        options.NamespacePrefixes[0].Prefix.ShouldBe("api/orders");
    }

    [Fact]
    public void prepend_prefix_handles_route_with_leading_slash()
    {
        var chain = HttpChain.ChainFor<RoutePrefixTestEndpoints>(x => x.GetItems());
        chain.RoutePattern!.RawText.ShouldBe("/items");

        RoutePrefixPolicy.PrependPrefix(chain, "api");
        chain.RoutePattern!.RawText.ShouldBe("/api/items");
    }

    [Fact]
    public void prepend_prefix_handles_route_with_parameters()
    {
        var chain = HttpChain.ChainFor<RoutePrefixTestEndpoints>(x => x.GetItem(0));
        chain.RoutePattern!.RawText.ShouldBe("/items/{id}");

        RoutePrefixPolicy.PrependPrefix(chain, "api");
        chain.RoutePattern!.RawText.ShouldBe("/api/items/{id}");
        chain.RoutePattern.Parameters.Count.ShouldBe(1);
        chain.RoutePattern.Parameters[0].Name.ShouldBe("id");
    }

    [Fact]
    public void attribute_prefix_is_trimmed()
    {
        var attr = new RoutePrefixAttribute("/v2/orders/");
        attr.Prefix.ShouldBe("v2/orders");
    }

    [Fact]
    public void attribute_prefix_throws_on_null()
    {
        Should.Throw<ArgumentNullException>(() => new RoutePrefixAttribute(null!));
    }

    [Fact]
    public void most_specific_namespace_wins()
    {
        var options = new WolverineHttpOptions();
        options.RoutePrefix("api", forEndpointsInNamespace: "Wolverine");
        options.RoutePrefix("api/http", forEndpointsInNamespace: "Wolverine.Http");
        options.RoutePrefix("api/http/tests", forEndpointsInNamespace: "Wolverine.Http.Tests");

        var chain = HttpChain.ChainFor<RoutePrefixTestEndpoints>(x => x.GetItems());

        var policy = new RoutePrefixPolicy(options);
        var prefix = policy.DeterminePrefix(chain);
        // Should match the most specific (longest) namespace
        prefix.ShouldBe("api/http/tests");
    }
}

// Test handler types
public class RoutePrefixTestEndpoints
{
    [WolverineGet("/items")]
    public string GetItems() => "items";

    [WolverineGet("/items/{id}")]
    public string GetItem(int id) => $"item {id}";
}

[RoutePrefix("v2/orders")]
public class PrefixedEndpoints
{
    [WolverineGet("/orders")]
    public string GetOrders() => "orders";
}
