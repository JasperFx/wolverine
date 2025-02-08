using IntegrationTests;
using JasperFx.CodeGeneration;
using Marten;
using Marten.Events;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

public class using_an_aggregate_that_handles_commands : PostgresqlContext, IDisposable
{
    private readonly IHost theHost;
    private readonly IDocumentStore theStore;
    private Guid theStreamId;

    public using_an_aggregate_that_handles_commands()
    {
        theHost = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = GetType().Assembly;
                opts.Services.AddMarten(o =>
                {
                    o.Connection(Servers.PostgresConnectionString);
                    o.Projections.Snapshot<SelfLetteredAggregate>(SnapshotLifecycle.Inline);
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).Start();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public void Dispose()
    {
        theHost?.Dispose();
    }

    internal async Task GivenAggregate()
    {
        await using var session = theStore.LightweightSession();
        var action = session.Events.StartStream<SelfLetteredAggregate>(new LetterStarted());
        await session.SaveChangesAsync();

        theStreamId = action.Id;
    }

    internal async Task<SelfLetteredAggregate> LoadAggregate()
    {
        await using var session = theStore.LightweightSession();
        return await session.LoadAsync<SelfLetteredAggregate>(theStreamId);
    }

    internal async Task OnAggregate(Action<SelfLetteredAggregate> assertions)
    {
        var aggregate = await LoadAggregate();
        assertions(aggregate);
    }

    [Fact]
    public async Task sync_one_event()
    {
        await GivenAggregate();
        await theHost.TrackActivity().SendMessageAndWaitAsync(new IncrementA2(theStreamId));

        await OnAggregate(a => a.ACount.ShouldBe(1));
    }

    [Fact]
    public async Task sync_many_events()
    {
        await GivenAggregate();
        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new IncrementMany2(theStreamId, ["A", "A", "B", "C", "C", "C"]));

        await OnAggregate(a =>
        {
            a.ACount.ShouldBe(2);
            a.BCount.ShouldBe(1);
            a.CCount.ShouldBe(3);
        });
    }

    [Fact]
    public async Task async_one_event()
    {
        await GivenAggregate();
        await theHost.TrackActivity().SendMessageAndWaitAsync(new IncrementB2(theStreamId));

        await OnAggregate(a => { a.BCount.ShouldBe(1); });
    }
}

public record IncrementA2(Guid SelfLetteredAggregateId);

public record IncrementB2(Guid SelfLetteredAggregateId);

public record IncrementC2(Guid SelfLetteredAggregateId);

public record IncrementMany2(Guid SelfLetteredAggregateId, string[] Letters);

[AggregateHandler]
public class SelfLetteredAggregate
{
    public SelfLetteredAggregate()
    {
    }

    public SelfLetteredAggregate(LetterStarted started)
    {
    }

    public Guid Id { get; set; }
    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }

    public void Apply(AEvent e)
    {
        ACount++;
    }

    public void Apply(BEvent e)
    {
        BCount++;
    }

    public void Apply(CEvent e)
    {
        CCount++;
    }

    public void Apply(DEvent e)
    {
        DCount++;
    }

    // Synchronous, one event, no other services
    public AEvent Handle(IncrementA2 command, ILogger<SelfLetteredAggregate> logger)
    {
        logger.ShouldNotBeNull();
        return new AEvent();
    }

    public void Handle(IncrementC2 command, IEventStream<SelfLetteredAggregate> stream)
    {
        stream.AppendOne(new CEvent());
    }

    // Asynchronous, one event, no other services
    public Task<BEvent> Handle(IncrementB2 command)
    {
        return Task.FromResult(new BEvent());
    }

    public IEnumerable<object> Handle(IncrementMany2 command)
    {
        foreach (var letter in command.Letters)
        {
            switch (letter)
            {
                case "A":
                    yield return new AEvent();
                    break;

                case "B":
                    yield return new BEvent();
                    break;

                case "C":
                    yield return new CEvent();
                    break;

                case "D":
                    yield return new CEvent();
                    break;
            }
        }
    }
}