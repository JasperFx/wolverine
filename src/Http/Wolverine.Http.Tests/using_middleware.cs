using Microsoft.Extensions.DependencyInjection;
using TestingSupport;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_middleware : IntegrationContext
{
    [Fact]
    public async Task using_basic_middleware()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Host.Scenario(x =>
        {
            x.Get.Url("/middleware/simple");

        });
        
        recorder.Actions.ShouldHaveTheSameElementsAs("Before", "Action", "After");
    }

    [Fact]
    public async Task using_intrinsic_middleware_for_a_compound_endpoint()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Host.Scenario(x =>
        {
            x.Get.Url("/middleware/intrinsic");

        });
        
        recorder.Actions.ShouldHaveTheSameElementsAs("Before", "Action", "After");
    }
}