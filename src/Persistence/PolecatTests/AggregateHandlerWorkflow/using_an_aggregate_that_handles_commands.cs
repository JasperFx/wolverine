using IntegrationTests;
using JasperFx.Events.Projections;
using JasperFx.CodeGeneration;
using JasperFx.Events;
using Polecat;
using Polecat.Events;
using Polecat.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests;

public class using_an_aggregate_that_handles_commands : IDisposable
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
                opts.Services.AddPolecat(o =>
                {
                    o.ConnectionString = Servers.SqlServerConnectionString;
                    o.DatabaseSchemaName = "self_letter";
                    o.Projections.Snapshot<SelfLetteredAggregate>(SnapshotLifecycle.Inline);
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).Start();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        ((DocumentStore)theStore).Database.ApplyAllConfiguredChangesToDatabaseAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        theHost?.Dispose();
    }

    internal async Task GivenAggregate()
    {
        await using var session = theStore.LightweightSession();
        var action = session.Events.StartStream<SelfLetteredAggregate>(new AggregateHandlerWorkflow.LetterStarted());
        await session.SaveChangesAsync();

        theStreamId = action.Id;
    }

    internal async Task<SelfLetteredAggregate?> LoadAggregate()
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

    public SelfLetteredAggregate(AggregateHandlerWorkflow.LetterStarted started)
    {
    }

    public Guid Id { get; set; }
    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }

    public void Apply(AggregateHandlerWorkflow.AEvent e)
    {
        ACount++;
    }

    public void Apply(AggregateHandlerWorkflow.BEvent e)
    {
        BCount++;
    }

    public void Apply(AggregateHandlerWorkflow.CEvent e)
    {
        CCount++;
    }

    public void Apply(AggregateHandlerWorkflow.DEvent e)
    {
        DCount++;
    }

    public AggregateHandlerWorkflow.AEvent Handle(IncrementA2 command, ILogger<SelfLetteredAggregate> logger)
    {
        logger.ShouldNotBeNull();
        return new AggregateHandlerWorkflow.AEvent();
    }

    public void Handle(IncrementC2 command, IEventStream<SelfLetteredAggregate> stream)
    {
        stream.AppendOne(new AggregateHandlerWorkflow.CEvent());
    }

    public Task<AggregateHandlerWorkflow.BEvent> Handle(IncrementB2 command)
    {
        return Task.FromResult(new AggregateHandlerWorkflow.BEvent());
    }

    public IEnumerable<object> Handle(IncrementMany2 command)
    {
        foreach (var letter in command.Letters)
        {
            switch (letter)
            {
                case "A":
                    yield return new AggregateHandlerWorkflow.AEvent();
                    break;

                case "B":
                    yield return new AggregateHandlerWorkflow.BEvent();
                    break;

                case "C":
                    yield return new AggregateHandlerWorkflow.CEvent();
                    break;

                case "D":
                    yield return new AggregateHandlerWorkflow.CEvent();
                    break;
            }
        }
    }
}
