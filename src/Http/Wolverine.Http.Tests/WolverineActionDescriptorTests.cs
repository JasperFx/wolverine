using JasperFx.Core.Reflection;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class WolverineActionDescriptorTests
{
    [Fact]
    public void set_controller_and_action_for_swashbuckle_defaults()
    {
        var chain = HttpChain.ChainFor<FakeEndpoint>(x => x.SayHello());
        var descriptor = new WolverineActionDescriptor(chain);
        descriptor.DisplayName.ShouldBe(chain.DisplayName);
        descriptor.RouteValues["controller"].ShouldBe(typeof(FakeEndpoint).FullNameInCode());
        descriptor.RouteValues["action"].ShouldBe(nameof(FakeEndpoint.SayHello));
    }
}