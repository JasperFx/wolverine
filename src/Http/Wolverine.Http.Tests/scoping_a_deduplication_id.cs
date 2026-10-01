using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Wolverine.Http.Runtime;
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
    public void an_authenticated_caller_with_no_name_scopes_by_name_identifier_then_sub()
    {
        HttpHandler.ScopeDeduplicationId(authenticated(new Claim(ClaimTypes.NameIdentifier, "id-1")), "k",
            DeduplicationScope.User, null).ShouldBe("0:|4:id-1|0:|k");

        HttpHandler.ScopeDeduplicationId(authenticated(new Claim("sub", "sub-1")), "k",
            DeduplicationScope.User, null).ShouldBe("0:|5:sub-1|0:|k");
    }

    [Fact]
    public void an_authenticated_caller_with_nothing_to_scope_by_is_refused()
    {
        // Otherwise every such caller would share the empty user, and one could be answered with another's response.
        Should.Throw<InvalidOperationException>(() =>
                HttpHandler.ScopeDeduplicationId(authenticated(new Claim("role", "admin")), "k",
                    DeduplicationScope.User, null))
            .Message.ShouldContain("no name, name identifier or 'sub' claim");
    }

    [Fact]
    public void the_stored_id_is_a_case_sensitive_ascii_hash()
    {
        // The store never compares the scoped key itself, so a case-insensitive collation cannot merge two users.
        var han = DeduplicatedResponses.StorageIdFor("0:|3:Han|0:|k");
        var lowerHan = DeduplicatedResponses.StorageIdFor("0:|3:han|0:|k");

        han.ShouldStartWith("sha256:");
        han.Length.ShouldBe(71);
        han.ShouldNotBe(lowerHan);
        han.ShouldBe(DeduplicatedResponses.StorageIdFor("0:|3:Han|0:|k"));
    }

    private static HttpContext authenticated(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "post";
        context.Request.Path = "/orders";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        return context;
    }
}
