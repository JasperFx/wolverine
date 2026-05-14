using IntegrationTests;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Bugs;

public class Bug_191_aggregate_handler_without_version : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(UpdatePcThingAggregateHandler));

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "bugs_191";
                }).IntegrateWithWolverine();
            }).StartAsync();

        await ((DocumentStore)_host.Services.GetRequiredService<IDocumentStore>()).Database.ApplyAllConfiguredChangesToDatabaseAsync();
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
        await using (var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<PcThing>(id, new PcThingStarted(id, "stuff"));
            await session.SaveChangesAsync();
        }

        await _host.InvokeMessageAndWaitAsync(new UpdatePcThing(id, "new stuff"));
    }

    public record PcThingStarted(Guid ThingId, string ThingStuff);

    public record UpdatePcThing(Guid PcThingId, string StuffToUpdate);

    public record PcThingUpdated(Guid ThingId, string UpdatedStuff);

    public class PcThing
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
        public string Stuff { get; set; } = string.Empty;

        public void Apply(PcThingStarted @event)
        {
            Id = @event.ThingId;
            Stuff = @event.ThingStuff;
        }

        public void Apply(PcThingUpdated @event)
        {
            Stuff = @event.UpdatedStuff;
        }
    }

    public static class UpdatePcThingAggregateHandler
    {
        public static IEnumerable<object> Handle(UpdatePcThing cmd, PcThing aggregate)
        {
            Console.WriteLine($"Loaded {nameof(aggregate)} version {aggregate.Version}");

            yield return new PcThingUpdated(cmd.PcThingId, cmd.StuffToUpdate);
        }
    }
}
