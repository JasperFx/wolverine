using Alba;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

// GH-4339: the third and last door into the GH-4308/GH-4314 silent-misbind class. A
// [FromQuery]/[FromHeader]/[FromRoute] parameter on a MIDDLEWARE Finally method got no HTTP binding
// at all: MiddlewarePolicy builds those MethodCalls without chain.ApplyParameterMatching (every
// sibling method kind -- Before, After, AfterCommit, OnException -- gets it), and they are nested
// inside the try/finally wrapper frame rather than laid out in Middleware, so neither the
// route-variable loop in HttpChain.DetermineFrames nor the GH-4314 postprocessor pass reached them.
// JasperFx's name-then-type fallback then bound `audit` AND `trace` to httpContext.TraceIdentifier --
// verified in the generated source before the fix -- while a parsed type died at codegen instead.
public class finally_query_header_route_binding_4339 : IntegrationContext
{
    public finally_query_header_route_binding_4339(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task finally_from_query_and_from_header_parameters_bind_from_the_wire()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Scenario(x =>
        {
            x.Get.Url("/middleware/finally-query-header?audit=abc&attempts=3");
            x.WithRequestHeader("x-trace", "t-42");
        });

        (await recorded(recorder, 2)).ShouldHaveTheSameElementsAs(
            "Action", "Finally: audit=abc, attempts=3, trace=t-42");
    }

    [Fact]
    public async Task absent_values_stay_absent_instead_of_binding_to_an_unrelated_variable()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        // The regression: with nothing on the wire, both string parameters used to receive
        // httpContext.TraceIdentifier, so this recorded a GUID-ish trace id in each slot.
        await Scenario(x => x.Get.Url("/middleware/finally-query-header"));

        (await recorded(recorder, 2)).ShouldHaveTheSameElementsAs(
            "Action", "Finally: audit=null, attempts=0, trace=null");
    }

    // The finals are built in two different places depending on whether the middleware also has a
    // Before: MiddlewarePolicy.wrapBeforeFrame for this one, buildFinals for the test above. Both had
    // to be fixed, and only a shape of each proves it.
    [Fact]
    public async Task a_middleware_that_also_has_a_before_binds_its_finally_the_same_way()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Scenario(x =>
        {
            x.Get.Url("/middleware/finally-with-before-query-header?audit=xyz");
            x.WithRequestHeader("x-trace", "t-7");
        });

        (await recorded(recorder, 3)).ShouldHaveTheSameElementsAs(
            "Before", "Action", "FinallyWithBefore: audit=xyz, trace=t-7");
    }

    // The GH-4308 half. A parsed route-bindable type has no same-typed variable to fall back to, so
    // this shape did not compile at all -- it failed `codegen preview` on WolverineWebApi, which is
    // why the endpoint also stays in the Nuke CodegenPreviewCommand's path.
    [Fact]
    public async Task finally_from_route_parameter_binds_the_parsed_route_value()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Scenario(x => x.Get.Url("/middleware/finally-route/1234"));

        (await recorded(recorder, 2)).ShouldHaveTheSameElementsAs("Action", "FinallyRoute: 1234");
    }

    // Both halves of the contract have to make the same claim, which is the whole point of this wave:
    // the generated code now reads these from the wire, so OpenAPI has to say so.
    [Fact]
    public void the_bound_finally_parameters_are_described_in_the_openapi_document()
    {
        var chain = HttpChains.ChainFor("GET", "/middleware/finally-query-header").ShouldNotBeNull();
        var description = chain.CreateApiDescription("GET");

        var parameters = description.ParameterDescriptions
            .Select(x => $"{x.Source.Id}:{x.Name}")
            .ToArray();

        parameters.ShouldContain("Query:audit");
        parameters.ShouldContain("Query:attempts");
        parameters.ShouldContain("Header:x-trace");
    }

    // A middleware Finally runs on the way out of the generated Handle method -- which is AFTER the
    // response body has been written, so the in-memory host can complete the Alba scenario before the
    // finally block has appended anything. Reading recorder.Actions the instant Scenario() returns is
    // therefore a race, and it is one that only shows up once a previous test has already warmed this
    // chain's compiled code: cold, codegen makes the request slow enough that the finally always wins.
    // Bisected out of a 2-in-5 intermittent failure, so wait for the effect rather than assume it.
    private static async Task<IReadOnlyList<string>> recorded(Recorder recorder, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (recorder.Actions.Count < count && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        return recorder.Actions.ToArray();
    }
}
