using Alba;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

// GH-4308: a postprocessor [FromQuery] parameter whose name collides with a route segment on a
// route-bindable type is claimed by the route. The OpenAPI side has said so since GH-3601 (only the
// Path parameter renders); this proves the generated code makes good on that claim instead of dying
// at codegen with an UnResolvableVariableException.
public class postprocessor_route_binding_4308 : IntegrationContext
{
    public postprocessor_route_binding_4308(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task a_postprocessor_parameter_claimed_by_the_route_binds_from_the_route_value()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        // The query string carries a conflicting value on the same name: the route's claim has to
        // win, exactly as the OpenAPI description documents it.
        await Scenario(x => x.Get.Url("/middleware/postprocessor-route/123?orderId=456"));

        recorder.Actions.ShouldHaveTheSameElementsAs("After: 123");
    }

    [Fact]
    public async Task an_unparseable_route_value_is_a_404_before_the_endpoint_runs()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Scenario(x =>
        {
            x.Get.Url("/middleware/postprocessor-route/not-a-long");
            x.StatusCodeShouldBe(404);
        });

        recorder.Actions.ShouldBeEmpty();
    }
}
