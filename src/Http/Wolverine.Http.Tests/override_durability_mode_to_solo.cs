using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Runtime;

namespace Wolverine.Http.Tests;

public class override_durability_mode_to_solo : IntegrationContext
{
    public override_durability_mode_to_solo(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void verify_that_testing_helper_works()
    {
        Host.Services.GetRequiredService<IWolverineRuntime>()
            .Options.Durability.Mode.ShouldBe(DurabilityMode.Solo);
    }
}