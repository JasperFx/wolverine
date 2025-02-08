using IntegrationTests;
using JasperFx.CodeGeneration;
using Marten;
using Marten.Events;
using Marten.Events.Projections;
using Marten.Exceptions;
using Marten.Internal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

public class marten_command_workflow_middleware : PostgresqlContext, IDisposable
{
    private readonly IHost theHost;
    private readonly IDocumentStore theStore;
    private Guid theStreamId;

    public marten_command_workflow_middleware()
    {
        theHost = WolverineHost.For(opts =>
        {
            opts.Services.AddMarten(opts =>
                {
                    opts.Connection(Servers.PostgresConnectionString);
                    opts.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Inline);
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();

            opts.Services.AddResourceSetupOnStartup();

            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public void Dispose()
    {
        theHost?.Dispose();
    }

    internal async Task GivenAggregate()
    {
        await using var session = theStore.LightweightSession();
        var action = session.Events.StartStream<LetterAggregate>(new LetterStarted());
        await session.SaveChangesAsync();

        theStreamId = action.Id;
    }

    internal async Task<LetterAggregate> LoadAggregate()
    {
        await using var session = theStore.LightweightSession();
        return await session.LoadAsync<LetterAggregate>(theStreamId);
    }

    internal async Task OnAggregate(Action<LetterAggregate> assertions)
    {
        var aggregate = await LoadAggregate();
        assertions(aggregate);
    }

    [Fact]
    public async Task sync_one_event()
    {
        await GivenAggregate();
        await theHost.TrackActivity().SendMessageAndWaitAsync(new IncrementA(theStreamId));

        await OnAggregate(a => a.ACount.ShouldBe(1));
    }

    [Fact]
    public async Task sync_many_events()
    {
        await GivenAggregate();
        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new IncrementMany(theStreamId, ["A", "A", "B", "C", "C", "C"]));

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
        await theHost.TrackActivity().SendMessageAndWaitAsync(new IncrementB(theStreamId));

        await OnAggregate(a => { a.BCount.ShouldBe(1); });
    }

    [Fact]
    public async Task async_many_events()
    {
        await GivenAggregate();
        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new IncrementManyAsync(theStreamId, ["A", "A", "B", "C", "C", "C"]));

        await OnAggregate(a =>
        {
            a.ACount.ShouldBe(2);
            a.BCount.ShouldBe(1);
            a.CCount.ShouldBe(3);
        });
    }

    [Fact]
    public async Task sync_use_event_stream_directly()
    {
        await GivenAggregate();
        await theHost.TrackActivity().SendMessageAndWaitAsync(new IncrementC(theStreamId));

        await OnAggregate(a => { a.CCount.ShouldBe(1); });
    }

    [Fact]
    public async Task async_use_event_stream_directly()
    {
        await GivenAggregate();
        await theHost.TrackActivity().SendMessageAndWaitAsync(new IncrementD(theStreamId));

        await OnAggregate(a => { a.DCount.ShouldBe(1); });
    }

    [Fact]
    public async Task use_exclusive_writes()
    {
        await GivenAggregate();
        await theHost.TrackActivity().SendMessageAndWaitAsync(new IncrementAB(theStreamId));

        await OnAggregate(a =>
        {
            a.ACount.ShouldBe(1);
            a.BCount.ShouldBe(1);
        });
    }

    [Fact]
    public async Task use_optimistic_writes_with_int_version()
    {
        await GivenAggregate();
        await theHost.TrackActivity().SendMessageAndWaitAsync(new IncrementBC(theStreamId, 1));

        await OnAggregate(a =>
        {
            a.BCount.ShouldBe(1);
            a.CCount.ShouldBe(1);
        });
    }

    [Fact]
    public async Task use_optimistic_writes_with_long_version()
    {
        await GivenAggregate();
        await theHost.TrackActivity().SendMessageAndWaitAsync(new IncrementCD(theStreamId, 1));

        await OnAggregate(a =>
        {
            a.CCount.ShouldBe(1);
            a.DCount.ShouldBe(1);
        });
    }

    // TODO -- this is a Marten implementation issue?
    // [Fact]
    // public async Task just_do_not_blow_up_if_no_event_is_returned()
    // {
    //     await GivenAggregate();
    //     await theHost.TrackActivity().SendMessageAndWaitAsync(new IncrementNone(theStreamId));
    //
    //     await OnAggregate(a =>
    //     {
    //         a.ACount.ShouldBe(0);
    //         a.BCount.ShouldBe(0);
    //         a.CCount.ShouldBe(0);
    //         a.DCount.ShouldBe(0);
    //     });
    // }
}

public class LetterStarted;

public class LetterAggregate
{
    public LetterAggregate()
    {
    }

    public LetterAggregate(LetterStarted started)
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
}

public static class SpecialLetterHandler
{
    // This can be done as a policy at the application level and not
    // on a handler by handler basis too
    [ScheduleRetry(typeof(ConcurrencyException), 1, 2, 5)]
    [AggregateHandler(ConcurrencyStyle.Exclusive)]
    public static IEnumerable<object> Handle(IncrementAB command, LetterAggregate aggregate)
    {
        command.LetterAggregateId.ShouldBe(aggregate.Id);
        yield return new AEvent();
        yield return new BEvent();
    }
}

public class LetterAggregateHandler
{
    // No event returned
    public AEvent Handle(IncrementNone command, LetterAggregate aggregate)
    {
        return null;
    }

    // Synchronous, one event, no other services
    public AEvent Handle(IncrementA command, LetterAggregate aggregate, IQuerySession session)
    {
        // Just proving that we're getting the same session that's used in the middleware
        session.ShouldBeOfType<LightweightSession>();
        command.LetterAggregateId.ShouldBe(aggregate.Id);
        return new AEvent();
    }

    // Asynchronous, one event, no other services
    public Task<BEvent> Handle(IncrementB command, LetterAggregate aggregate, ILogger<LetterAggregateHandler> logger)
    {
        command.LetterAggregateId.ShouldBe(aggregate.Id);
        logger.ShouldNotBeNull();
        return Task.FromResult(new BEvent());
    }

    // Synchronous, many events
    public IEnumerable<object> Handle(IncrementMany command, LetterAggregate aggregate, IDocumentSession session)
    {
        // Just proving that we're getting the same session that's used in the middleware
        session.ShouldBeOfType<LightweightSession>();
        command.LetterAggregateId.ShouldBe(aggregate.Id);
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

    public Task<object[]> Handle(IncrementManyAsync command, LetterAggregate aggregate, IDocumentSession session)
    {
        command.LetterAggregateId.ShouldBe(aggregate.Id);
        var events = Handle(new IncrementMany(command.LetterAggregateId, command.Letters), aggregate, session);
        return Task.FromResult(events.ToArray());
    }

    public void Handle(IncrementC command, IEventStream<LetterAggregate> stream)
    {
        command.LetterAggregateId.ShouldBe(stream.Aggregate.Id);
        stream.AppendOne(new CEvent());
    }

    public Task Handle(IncrementD command, IEventStream<LetterAggregate> stream)
    {
        command.LetterAggregateId.ShouldBe(stream.Aggregate.Id);
        stream.AppendOne(new DEvent());
        return Task.CompletedTask;
    }

    public IEnumerable<object> Handle(IncrementBC command, LetterAggregate aggregate)
    {
        command.LetterAggregateId.ShouldBe(aggregate.Id);
        yield return new BEvent();
        yield return new CEvent();
    }

    public (CEvent, DEvent) Handle(IncrementCD command, LetterAggregate aggregate)
    {
        command.LetterAggregateId.ShouldBe(aggregate.Id);
        return (new CEvent(), new DEvent());
    }
}

public record IncrementNone(Guid LetterAggregateId);

public record IncrementA(Guid LetterAggregateId);

public record IncrementB(Guid LetterAggregateId);

public record IncrementC(Guid LetterAggregateId);

public record IncrementD(Guid LetterAggregateId);

public record IncrementAB(Guid LetterAggregateId);

public record IncrementBC(Guid LetterAggregateId, int Version);

public record IncrementCD(Guid LetterAggregateId, long Version);

public record IncrementMany(Guid LetterAggregateId, string[] Letters);

public record IncrementManyAsync(Guid LetterAggregateId, string[] Letters);

public record AEvent;

public record BEvent;

public record CEvent;

public record DEvent;