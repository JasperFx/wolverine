using IntegrationTests;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.Core;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_191_marten_aggregate_handler_command_should_not_require_version : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(UpdateThingAggregateHandler));

                opts.Services.AddMarten(marten =>
                {
                    marten.Connection(Servers.PostgresConnectionString);
                    marten.DatabaseSchemaName = "bugs";
                    marten.AutoCreateSchemaObjects = AutoCreate.All;
                }).IntegrateWithWolverine();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task execute_without_code_compilation_errors()
    {
        var id = Guid.NewGuid();
        using (var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<Thing>(id, new ThingStarted(id, "stuff"));
            await session.SaveChangesAsync();
        }

        await _host.InvokeMessageAndWaitAsync(new UpdateThing(id, "new stuff"));
    }

    public record ThingStarted(Guid ThingId, string ThingStuff);

    public record UpdateThing(Guid ThingId, string StuffToUpdate);

    public record ThingUpdated(Guid ThingId, string UpdatedStuff);

    public class Thing
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
        public string Stuff { get; set; } = string.Empty;

        public void Apply(ThingStarted @event)
        {
            Id = @event.ThingId;
            Stuff = @event.ThingStuff;
        }

        public void Apply(ThingUpdated @event)
        {
            Stuff = @event.UpdatedStuff;
        }
    }

    public static class UpdateThingAggregateHandler
    {
        public static IEnumerable<object> Handle(UpdateThing cmd, Thing aggregate)
        {
            Console.WriteLine($"Loaded {nameof(aggregate)} version {aggregate.Version}");

            yield return new ThingUpdated(cmd.ThingId, cmd.StuffToUpdate);
        }
    }
}