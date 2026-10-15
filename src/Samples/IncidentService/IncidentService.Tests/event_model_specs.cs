using JasperFx.Events.EventModeling;
using Shouldly;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace IncidentService.Tests;

/// <summary>
/// GH-5004 (jasperfx#997): <c>--specs</c> handed this very assembly, whose manifest Bobcat.Generators wrote
/// at build time, links the incident service's slices to the specifications in <see cref="IncidentSpecs" />.
/// </summary>
[Collection("integration")]
public class event_model_specs(AppFixture fixture)
{
    [Fact]
    public async Task the_generated_manifest_links_each_command_slice_to_its_specifications()
    {
        var set = await WolverineEventModelExport.AssembleSetAsync(fixture.Host!.Services,
            token: TestContext.Current.CancellationToken);

        // Read from disk, exactly as the commands do with the path --specs is given
        var specifications = WolverineEventModelExport.LoadSpecifications([typeof(IncidentSpec).Assembly.Location]);

        var model = WolverineEventModelExport.LinkSpecifications(set, specifications).Sole!;

        model.Slices.Single(x => x.Name == nameof(LogIncident)).Specifications.Select(x => x.Identity)
            .ShouldBe(["Logging an incident/a new incident starts its own stream"]);

        model.Slices.Single(x => x.Name == nameof(CategoriseIncident)).Specifications.Select(x => x.Identity)
            .ShouldBe(["Categorising an incident/an open incident is categorised",
                "Categorising an incident/a closed incident is refused"], ignoreOrder: true);

        model.Slices.Single(x => x.Name == nameof(CloseIncident)).Specifications.Count.ShouldBe(2);

        // No spec sends ArchiveIncident, so that slice is left as it was
        model.Slices.Single(x => x.Name == nameof(ArchiveIncident)).Specifications.ShouldBeEmpty();
        model.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.UnresolvedSpecification);
    }
}
