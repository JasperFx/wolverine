using Alba;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

// GH-4314: a postprocessor [FromQuery]/[FromHeader] parameter that does not collide with a route
// segment must bind from the query string / header, exactly as the OpenAPI description has claimed
// since GH-3601. Before the fix nothing produced a variable for these parameters, and JasperFx's
// name-then-type fallback silently bound `audit` to the endpoint's response body (the only other
// string in the chain) — it compiled, so nothing caught it.
public class postprocessor_query_header_binding_4314 : IntegrationContext
{
    public postprocessor_query_header_binding_4314(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task postprocessor_from_query_and_from_header_parameters_bind_from_the_wire()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Scenario(x =>
        {
            x.Get.Url("/middleware/postprocessor-query-header?audit=abc&attempts=3");
            x.WithRequestHeader("x-trace", "t-42");
        });

        recorder.Actions.ShouldHaveTheSameElementsAs("After: audit=abc, attempts=3, trace=t-42");
    }

    [Fact]
    public async Task absent_values_stay_absent_instead_of_binding_to_an_unrelated_variable()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        // The regression: with no query string at all, the old fallback handed `audit` the
        // endpoint's response body, so this used to record audit=ok
        await Scenario(x => x.Get.Url("/middleware/postprocessor-query-header"));

        recorder.Actions.ShouldHaveTheSameElementsAs("After: audit=null, attempts=0, trace=null");
    }
}
