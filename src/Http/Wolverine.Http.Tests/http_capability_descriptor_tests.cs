using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Xunit;

namespace Wolverine.Http.Tests;

public class http_capability_descriptor_tests : IntegrationContext
{
    public http_capability_descriptor_tests(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void does_not_emit_the_redundant_routes_set()
    {
        // Precondition: the app really does have routes, so the dropped "Routes" set
        // would otherwise have been populated — this proves the assertion has teeth.
        HttpChains.Chains.ShouldNotBeEmpty();

        var description = Host.Services.GetServices<ICapabilityDescriptor>()
            .Select(x => x.Describe())
            .Single(x => x.Subject == "Wolverine.Http");

        // GH-3009: the per-route "Routes" set (~96 KB on a Topicus-scale graph) is dropped —
        // it was fully redundant with HttpGraphs[*].Chains, which the SPA already reads.
        description.Sets.ContainsKey("Routes").ShouldBeFalse();

        // ...but the small core capability content is unaffected.
        description.Tags.ShouldContain("http");
        description.Properties.ShouldContain(x => x.Name == nameof(WolverineHttpOptions.WarmUpRoutes));
    }
}
