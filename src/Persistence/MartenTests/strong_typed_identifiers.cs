using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StronglyTypedIds;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;

namespace MartenTests;

public class strong_typed_identifiers : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "knobs";
                }).IntegrateWithWolverine();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task use_strong_typed_identifier_with_single_entity_attribute()
    {
        var knob1 = new Knob() { Name = "Single" };
        using var session = _host.DocumentStore().LightweightSession();
        session.Store(knob1);
        await session.SaveChangesAsync();

        await _host.InvokeAsync(new TwistKnob(knob1.Id));
    }

    [Fact]
    public async Task use_with_multiple_entities_so_it_has_to_use_batch_querying()
    {
        var knob1 = new Knob() { Name = "One" };
        var knob2 = new Knob() { Name = "Two" };
        using var session = _host.DocumentStore().LightweightSession();
        session.Store(knob1, knob2);
        await session.SaveChangesAsync();
        
        await _host.InvokeAsync(new TwistOneThenAnother(knob1.Id, knob2.Id));
    }
}

[StronglyTypedId(Template.Guid)]
public readonly partial struct KnobId;

public class Knob
{
    public KnobId Id { get; set; }
    public string Name { get; set; }
}

public record TwistKnob(KnobId Id);
public record TwistOneThenAnother(KnobId Id1, KnobId Id2);

public static class KnobHandler
{
    public static void Handle(TwistKnob command, [Entity] Knob knob)
    {
        knob.ShouldNotBeNull();
        knob.Name.ShouldBe("Single");
    }

    public static void Handle(
        TwistOneThenAnother command,
        [Entity("Id1")] Knob knob1,
        [Entity("Id2")] Knob knob2
        
        )
    {
        knob1.Name.ShouldBe("One");
        knob2.Name.ShouldBe("Two");
    }
}