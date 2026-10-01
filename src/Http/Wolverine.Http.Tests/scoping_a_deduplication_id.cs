using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Wolverine.Http.Tests;

// GH-4742. How a [DeduplicatedWithResponse] key is scoped.
public class scoping_a_deduplication_id
{
    private static HttpContext context(string? user = null, string path = "/orders")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "post";
        context.Request.Path = path;

        if (user != null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "TestAuth"));
        }

        return context;
    }

    [Fact]
    public void a_missing_key_passes_through()
    {
        HttpHandler.ScopeDeduplicationId(context("han"), null, DeduplicationScope.User, null).ShouldBeNull();
        HttpHandler.ScopeDeduplicationId(context("han"), " ", DeduplicationScope.User, null).ShouldBe(" ");
    }

    [Fact]
    public void each_selected_part_is_folded_in()
    {
        HttpHandler.ScopeDeduplicationId(context("han"), "k",
                DeduplicationScope.Tenant | DeduplicationScope.User | DeduplicationScope.Endpoint, "red")
            .ShouldBe("3:red|3:han|12:POST /orders|k");
    }

    [Fact]
    public void an_unselected_part_is_empty()
    {
        HttpHandler.ScopeDeduplicationId(context("han"), "k", DeduplicationScope.Endpoint, "red")
            .ShouldBe("0:|0:|12:POST /orders|k");
    }

    [Fact]
    public void a_separator_inside_a_part_cannot_shift_the_others()
    {
        var tenantHoldsTheSeparator = HttpHandler.ScopeDeduplicationId(context("b"), "k",
            DeduplicationScope.Tenant | DeduplicationScope.User, "a|1:");
        var userHoldsIt = HttpHandler.ScopeDeduplicationId(context("1:b"), "k",
            DeduplicationScope.Tenant | DeduplicationScope.User, "a|");

        tenantHoldsTheSeparator.ShouldNotBe(userHoldsIt);
    }

    [Fact]
    public void an_anonymous_caller_scopes_to_the_empty_user()
    {
        HttpHandler.ScopeDeduplicationId(context(), "k", DeduplicationScope.User, null).ShouldBe("0:|0:|0:|k");
    }

    [Fact]
    public void a_long_scoped_id_is_hashed_rather_than_truncated()
    {
        var a = HttpHandler.ScopeDeduplicationId(context(), new string('a', 300), DeduplicationScope.Endpoint, null)!;
        var b = HttpHandler.ScopeDeduplicationId(context(), new string('a', 299) + "b", DeduplicationScope.Endpoint, null)!;

        a.ShouldStartWith("sha256:");
        a.Length.ShouldBeLessThanOrEqualTo(250);
        a.ShouldNotBe(b);
    }
}
