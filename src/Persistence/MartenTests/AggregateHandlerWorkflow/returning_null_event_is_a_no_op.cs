using IntegrationTests;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

// GH-4309: an aggregate handler that sometimes has nothing to append says so by returning a
// null event — the exact shape a null cascaded message has always had. Before the fix, the
// generated code passed the null straight into IEventStream.AppendOne, which throws
// ArgumentNullException from inside the store.
public class returning_null_event_is_a_no_op : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private Guid theStreamId;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Inline);
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(MaybeRaiseAHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();

        await using var session = theStore.LightweightSession();
        theStreamId = session.Events.StartStream<LetterAggregate>(new LetterStarted()).Id;
        await session.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task a_null_event_return_appends_nothing_and_does_not_blow_up()
    {
        await theHost.InvokeMessageAndWaitAsync(new MaybeRaiseA(theStreamId, Emit: false));

        await using var session = theStore.LightweightSession();
        var aggregate = await session.Events.AggregateStreamAsync<LetterAggregate>(theStreamId, token: TestContext.Current.CancellationToken);
        aggregate!.ACount.ShouldBe(0);
    }

    [Fact]
    public async Task a_non_null_event_return_still_appends()
    {
        await theHost.InvokeMessageAndWaitAsync(new MaybeRaiseA(theStreamId, Emit: true));

        await using var session = theStore.LightweightSession();
        var aggregate = await session.Events.AggregateStreamAsync<LetterAggregate>(theStreamId, token: TestContext.Current.CancellationToken);
        aggregate!.ACount.ShouldBe(1);
    }
}

public record MaybeRaiseA(Guid LetterAggregateId, bool Emit);

public static class MaybeRaiseAHandler
{
    [AggregateHandler]
    public static AEvent? Handle(MaybeRaiseA command, LetterAggregate aggregate)
        => command.Emit ? new AEvent() : null;
}
