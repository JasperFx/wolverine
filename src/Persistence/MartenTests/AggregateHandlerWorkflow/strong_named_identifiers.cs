using System.Diagnostics;
using System.Text.Json.Serialization;
using IntegrationTests;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StronglyTypedIds;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

public class strong_named_identifiers : IAsyncLifetime
{
    private IHost theHost;
    
    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "strong_named";
                }).IntegrateWithWolverine();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
    }

    [Fact]
    public async Task use_read_aggregate_by_itself()
    {
        var streamId = Guid.NewGuid();
        using var session = theHost.DocumentStore().LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(streamId, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());
        await session.SaveChangesAsync();

        var bus = theHost.MessageBus();
        var aggregate = await bus.InvokeAsync<StrongLetterAggregate>(new FetchCounts(new LetterId(streamId)));
        
        aggregate.ACount.ShouldBe(1);
        aggregate.BCount.ShouldBe(1);
        aggregate.CCount.ShouldBe(2);
    }

    [Fact]
    public async Task single_usage_of_write_aggregate()
    {
        var streamId = Guid.NewGuid();
        using var session = theHost.DocumentStore().LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(streamId, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());
        await session.SaveChangesAsync();

        await theHost.InvokeAsync(new IncrementStrongA(new LetterId(streamId)));

        var bus = theHost.MessageBus();
        var aggregate = await bus.InvokeAsync<StrongLetterAggregate>(new FetchCounts(new LetterId(streamId)));
        
        aggregate.ACount.ShouldBe(2);
        aggregate.BCount.ShouldBe(1);
        aggregate.CCount.ShouldBe(2);
    }

    [Fact]
    public async Task batch_query_usage_of_write_aggregate()
    {
        var stream1Id = Guid.NewGuid();
        var stream2Id = Guid.NewGuid();
        using var session = theHost.DocumentStore().LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(stream1Id, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());
        
        session.Events.StartStream<StrongLetterAggregate>(stream2Id, new AEvent(), new BEvent(), new BEvent(),
            new AEvent());
        await session.SaveChangesAsync();

        await theHost.InvokeMessageAndWaitAsync(new IncrementBOnBoth(new LetterId(stream1Id), new LetterId(stream2Id)));

        var aggregate1 = await session.Events.FetchLatest<StrongLetterAggregate>(stream1Id);
        aggregate1.BCount.ShouldBe(2);
        
        var aggregate2 = await session.Events.FetchLatest<StrongLetterAggregate>(stream2Id);
        aggregate2.BCount.ShouldBe(3);
        
    }

    [Fact]
    public async Task batch_query_with_both_read_and_write_aggregate()
    {
        var stream1Id = Guid.NewGuid();
        var stream2Id = Guid.NewGuid();
        using var session = theHost.DocumentStore().LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(stream1Id, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());
        
        session.Events.StartStream<StrongLetterAggregate>(stream2Id, new AEvent(), new BEvent(), new BEvent(),
            new AEvent(), new DEvent());
        await session.SaveChangesAsync();

        await theHost.InvokeMessageAndWaitAsync(new AddFrom(new LetterId(stream1Id), new LetterId(stream2Id)));

        var aggregate1 = await session.Events.FetchLatest<StrongLetterAggregate>(stream1Id);
        aggregate1.BCount.ShouldBe(3);
        aggregate1.ACount.ShouldBe(3);
        aggregate1.DCount.ShouldBe(1);
        
        var aggregate2 = await session.Events.FetchLatest<StrongLetterAggregate>(stream2Id);
        aggregate2.BCount.ShouldBe(2);
    }

    
}

#region sample_using_strong_typed_identifier_with_aggregate_handler_workflow

public record IncrementStrongA(LetterId Id);

public record AddFrom(LetterId Id1, LetterId Id2);

public record IncrementBOnBoth(LetterId Id1, LetterId Id2);

public record FetchCounts(LetterId Id);

public static class StrongLetterHandler
{
    public static StrongLetterAggregate Handle(FetchCounts counts,
        [ReadAggregate] StrongLetterAggregate aggregate) => aggregate;

    public static AEvent Handle(IncrementStrongA command, [WriteAggregate] StrongLetterAggregate aggregate)
    {
        return new();
    }

    public static void Handle(
        IncrementBOnBoth command,
        [WriteAggregate(nameof(IncrementBOnBoth.Id1))] IEventStream<StrongLetterAggregate> stream1,
        [WriteAggregate(nameof(IncrementBOnBoth.Id2))] IEventStream<StrongLetterAggregate> stream2
    )
    {
        stream1.AppendOne(new BEvent());
        stream2.AppendOne(new BEvent());
    }

    public static IEnumerable<object> Handle(
        AddFrom command,
        [WriteAggregate(nameof(AddFrom.Id1))] StrongLetterAggregate _,
        [ReadAggregate(nameof(AddFrom.Id2))] StrongLetterAggregate readOnly)
    {
        for (int i = 0; i < readOnly.ACount; i++)
        {
            yield return new AEvent();
        }
        
        for (int i = 0; i < readOnly.BCount; i++)
        {
            yield return new BEvent();
        }
        
        for (int i = 0; i < readOnly.CCount; i++)
        {
            yield return new CEvent();
        }
        
        for (int i = 0; i < readOnly.DCount; i++)
        {
            yield return new DEvent();
        }
    }
}

    #endregion



#region sample_strong_typed_identifier_with_aggregate

[StronglyTypedId(Template.Guid)]
public readonly partial struct LetterId;

public class StrongLetterAggregate
{
    public StrongLetterAggregate()
    {
    }

    public LetterId Id { get; set; }

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }

    public void Apply(AEvent _) => ACount++;
    public void Apply(BEvent _) => BCount++;
    public void Apply(CEvent _) => CCount++;
    public void Apply(DEvent _) => DCount++;
}

#endregion
