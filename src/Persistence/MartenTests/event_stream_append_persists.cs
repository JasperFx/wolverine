using IntegrationTests;
using JasperFx.Resources;
using Marten;
using JasperFx.Events;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

// GH-3032: a compound handler that loads an IEventStream<T> via FetchForWriting and appends to it must
// persist even without opts.Policies.AutoApplyTransactions() - consistent with single IMartenOp returns
// (GH-3025) and [AggregateHandler]. Previously the append was silently dropped (no SaveChangesAsync).
public class event_stream_append_persists : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "event_stream_3032";
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                // Deliberately NO opts.Policies.AutoApplyTransactions().
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AppendViaStreamHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task compound_handler_event_stream_append_persists_without_auto_transactions()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new AppendViaStreamCommand(id));

        await using var session = theStore.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);
        events.Count.ShouldBe(1); // was 0 (append dropped) before GH-3032
    }
}

public record AppendViaStreamCommand(Guid Id);

public record StreamThingHappened(int Amount);

public class StreamThing
{
    public Guid Id { get; set; }
    public int Total { get; set; }
    public void Apply(StreamThingHappened e) => Total += e.Amount;
}

public static class AppendViaStreamHandler
{
    public static Task<IEventStream<StreamThing>> LoadAsync(
        AppendViaStreamCommand command, IDocumentSession session, CancellationToken cancellation)
        => session.Events.FetchForWriting<StreamThing>(command.Id, cancellation);

    public static void Handle(AppendViaStreamCommand command, IEventStream<StreamThing> stream)
        => stream.AppendOne(new StreamThingHappened(5));
}
