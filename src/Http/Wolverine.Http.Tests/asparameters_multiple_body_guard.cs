using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Wolverine.Attributes;

namespace Wolverine.Http.Tests;

// GH-3135 WS4: an [AsParameters] type that declares more than one [FromBody] member must fail fast
// with a clear message rather than silently reusing the first member's deserialization variable.
// These endpoint types live in the test assembly so the application host never discovers them
// (which would break the shared fixture at startup).
public class asparameters_multiple_body_guard
{
    [Fact]
    public void throws_when_more_than_one_from_body_member()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<TwoBodyEndpoint>(x => x.Post(null!)));

        ex.Message.ShouldContain("more than one [FromBody]");
    }

    [Fact]
    public void single_from_body_member_is_allowed()
    {
        // Sanity: the guard does not trip on the legitimate single-body case.
        Should.NotThrow(() => HttpChain.ChainFor<SingleBodyEndpoint>(x => x.Post(null!)));
    }
}

public record FirstBody(string Name);

public record SecondBody(int Age);

public record TwoBodyCommand([FromBody] FirstBody First, [FromBody] SecondBody Second);

public class TwoBodyEndpoint
{
    // [WolverineIgnore] keeps the host from discovering this intentionally-invalid endpoint at
    // startup (which would throw the guard and break every test-assembly-scoped host). ChainFor
    // builds it directly, bypassing discovery, so the guard is still exercised under test.
    [WolverineIgnore]
    [WolverinePost("/3135/two-body")]
    public string Post([AsParameters] TwoBodyCommand command) => "nope";
}

public record SingleBodyCommand([FromBody] FirstBody Body, [FromQuery] string? Name);

public class SingleBodyEndpoint
{
    [WolverineIgnore]
    [WolverinePost("/3135/single-body")]
    public string Post([AsParameters] SingleBodyCommand command) => "ok";
}
