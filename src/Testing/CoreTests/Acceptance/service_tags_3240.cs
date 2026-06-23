using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.Capabilities;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// GH-3240: user-defined service-level tags on WolverineOptions flow to ServiceCapabilities.Tags so CritterWatch
// can filter related services by the operator's own labels.
public class service_tags_3240
{
    [Fact]
    public async Task user_tags_flow_to_service_capabilities()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Tags.Add("team:payments");
                opts.Tags.Add("tier:critical");
            }).StartAsync();

        var capabilities = await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, CancellationToken.None);

        capabilities.Tags.ShouldBe(["team:payments", "tier:critical"]);
    }

    [Fact]
    public async Task tags_default_to_empty()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine().StartAsync();

        var capabilities = await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, CancellationToken.None);

        capabilities.Tags.ShouldBeEmpty();
    }
}
