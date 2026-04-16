using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using StronglyTypedIds;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Persistence;

namespace PolecatTests;

public class strong_typed_identifiers : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "knobs";
                }).IntegrateWithWolverine();
            }).StartAsync();

        await ((DocumentStore)_host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task use_strong_typed_identifier_with_single_entity_attribute()
    {
        var knob1 = new PcKnob { Name = "Single" };
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(knob1);
        await session.SaveChangesAsync();

        await _host.InvokeAsync(new TwistPcKnob(knob1.Id));
    }

    [Fact]
    public async Task use_with_multiple_entities_so_it_has_to_use_batch_querying()
    {
        var knob1 = new PcKnob { Name = "One" };
        var knob2 = new PcKnob { Name = "Two" };
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(knob1, knob2);
        await session.SaveChangesAsync();

        await _host.InvokeAsync(new TwistOneThenAnotherPcKnob(knob1.Id, knob2.Id));
    }
}

[StronglyTypedId(Template.Guid)]
public readonly partial struct PcKnobId;

public class PcKnob
{
    public PcKnobId Id { get; set; }
    public string Name { get; set; }
}

public record TwistPcKnob(PcKnobId Id);
public record TwistOneThenAnotherPcKnob(PcKnobId Id1, PcKnobId Id2);

public static class PcKnobHandler
{
    public static void Handle(TwistPcKnob command, [Entity] PcKnob knob)
    {
        knob.ShouldNotBeNull();
        knob.Name.ShouldBe("Single");
    }

    public static void Handle(
        TwistOneThenAnotherPcKnob command,
        [Entity("Id1")] PcKnob knob1,
        [Entity("Id2")] PcKnob knob2
    )
    {
        knob1.Name.ShouldBe("One");
        knob2.Name.ShouldBe("Two");
    }
}
