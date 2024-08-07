using Alba;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.ComplianceTests;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_middleware : IntegrationContext
{
    public using_middleware(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task using_basic_middleware()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Scenario(x => { x.Get.Url("/middleware/simple"); });

        recorder.Actions.ShouldHaveTheSameElementsAs("Before", "Action", "After");
    }

    [Fact]
    public async Task using_intrinsic_middleware_for_a_compound_endpoint()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Scenario(x => { x.Get.Url("/middleware/intrinsic"); });

        recorder.Actions.ShouldHaveTheSameElementsAs("Before", "Action", "After");
    }

    [Fact]
    public async Task middleware_with_iresult_filter_happy_path()
    {
        await Scenario(x =>
        {
            x.Post.Json(new AuthenticatedRequest { Authenticated = true }, JsonStyle.MinimalApi)
                .ToUrl("/authenticated");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task middleware_with_iresult_filter_sad_path()
    {
        await Scenario(x =>
        {
            x.Post.Json(new AuthenticatedRequest { Authenticated = false }).ToUrl("/authenticated");
            x.StatusCodeShouldBe(401);
        });
    }
}