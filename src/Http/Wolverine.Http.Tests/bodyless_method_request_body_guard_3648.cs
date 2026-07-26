using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Wolverine.Attributes;

namespace Wolverine.Http.Tests;

// GH-3648: an endpoint that advertises a request body on a method that cannot carry one is unreachable.
// ASP.NET Core's AcceptsMatcherPolicy compiles IAcceptsMetadata into content-type edges in the route
// matcher, so the endpoint is dropped from candidate selection and every request returns a bare 404 --
// not a 415, with nothing logged. That silence is what made GH-3591/GH-3630 so hard to diagnose, so this
// now fails fast at startup instead.
//
// As with the GH-3135 guard above, these intentionally-invalid endpoints carry [WolverineIgnore] so the
// shared application host never discovers them; ChainFor builds them directly.
public class bodyless_method_request_body_guard_3648
{
    [Fact]
    public void throws_for_a_get_decorated_with_accepts_content_type()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<GetWithAcceptsContentType>(x => x.Get(null!)));

        ex.Message.ShouldContain("GET");
        ex.Message.ShouldContain("would return 404");
        ex.Message.ShouldContain("[AcceptsContentType(\"application/json\")]");
    }

    [Fact]
    public void throws_for_a_get_whose_asparameters_type_binds_from_a_form()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<GetWithFormMembers>(x => x.Get(null!)));

        ex.Message.ShouldContain("bind from a form");
    }

    [Fact]
    public void throws_for_a_get_taking_a_file_parameter()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<GetWithFileParameter>(x => x.Get(null!)));

        ex.Message.ShouldContain("read from a form");
    }

    [Fact]
    public void the_same_shapes_are_fine_on_post()
    {
        // The guard is about the HTTP method, not the binding. Every shape above is legitimate on POST.
        Should.NotThrow(() => HttpChain.ChainFor<PostWithAcceptsContentType>(x => x.Post(null!)));
        Should.NotThrow(() => HttpChain.ChainFor<PostWithFormMembers>(x => x.Post(null!)));
    }

    [Fact]
    public void a_query_only_get_is_still_allowed()
    {
        // After GH-3630 a query-only [AsParameters] GET emits no Accepts metadata at all, so it must not
        // trip this guard -- that combination is the single most common Wolverine HTTP endpoint shape.
        Should.NotThrow(() => HttpChain.ChainFor<GetWithQueryOnly>(x => x.Get(null!)));
    }

    [Fact]
    public void a_delete_with_a_body_is_deliberately_still_allowed()
    {
        // DELETE with a request body has no defined semantics but is not forbidden, and some APIs rely on
        // it. Failing those at startup would be a gratuitous break, so the guard covers GET/HEAD only.
        Should.NotThrow(() => HttpChain.ChainFor<DeleteWithAcceptsContentType>(x => x.Delete(null!)));
    }
}

public record Gh3648Payload(string Name);

public record Gh3648FormCommand([FromForm] string Name);

public record Gh3648QueryCommand([FromQuery] string? Name);

public class GetWithAcceptsContentType
{
    [WolverineIgnore]
    [WolverineGet("/3648/get-accepts"), AcceptsContentType("application/json")]
    public string Get(Gh3648Payload payload) => "nope";
}

public class GetWithFormMembers
{
    [WolverineIgnore]
    [WolverineGet("/3648/get-form")]
    public string Get([AsParameters] Gh3648FormCommand command) => "nope";
}

public class GetWithFileParameter
{
    [WolverineIgnore]
    [WolverineGet("/3648/get-file")]
    public string Get(IFormFile file) => "nope";
}

public class GetWithQueryOnly
{
    [WolverineIgnore]
    [WolverineGet("/3648/get-query")]
    public string Get([AsParameters] Gh3648QueryCommand command) => "ok";
}

public class PostWithAcceptsContentType
{
    [WolverineIgnore]
    [WolverinePost("/3648/post-accepts"), AcceptsContentType("application/json")]
    public string Post(Gh3648Payload payload) => "ok";
}

public class PostWithFormMembers
{
    [WolverineIgnore]
    [WolverinePost("/3648/post-form")]
    public string Post([AsParameters] Gh3648FormCommand command) => "ok";
}

public class DeleteWithAcceptsContentType
{
    [WolverineIgnore]
    [WolverineDelete("/3648/delete-accepts"), AcceptsContentType("application/json")]
    public string Delete(Gh3648Payload payload) => "ok";
}
