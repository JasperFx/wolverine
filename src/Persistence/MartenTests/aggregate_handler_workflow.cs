using System.Diagnostics;
using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Marten;
using Marten.Events;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

public class aggregate_handler_workflow: PostgresqlContext, IAsyncLifetime
{
    private IHost theHost;
    private IDocumentStore theStore;
    private Guid theStreamId;

    public async Task InitializeAsync()
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

                opts.Services.AddResourceSetupOnStartup();

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
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
    public async Task events_then_response_invoke_no_return()
    {
        await GivenAggregate();

        var tracked = await theHost.InvokeMessageAndWaitAsync(new RaiseABC(theStreamId));

        tracked.Sent.SingleMessage<Response>().ACount.ShouldBe(1);

        await OnAggregate(a =>
        {
            a.ACount.ShouldBe(1);
            a.BCount.ShouldBe(1);
            a.CCount.ShouldBe(1);
        });
    }

    [Fact]
    public async Task events_then_response_invoke_with_return()
    {
        await GivenAggregate();

        var (tracked, response) = await theHost.InvokeMessageAndWaitAsync<Response>(new RaiseABC(theStreamId));
        response.ACount.ShouldBe(1);
        response.BCount.ShouldBe(1);
        response.CCount.ShouldBe(1);

        await OnAggregate(a =>
        {
            a.ACount.ShouldBe(1);
            a.BCount.ShouldBe(1);
            a.CCount.ShouldBe(1);
        });
    }

    [Fact] public async Task response_then_events_invoke_no_return()
    {
        await GivenAggregate();

        var tracked = await theHost.InvokeMessageAndWaitAsync(new RaiseAABCC(theStreamId));

        tracked.Sent.SingleMessage<Response>().ACount.ShouldBe(2);

        await OnAggregate(a =>
        {
            a.ACount.ShouldBe(2);
            a.BCount.ShouldBe(1);
            a.CCount.ShouldBe(2);
        });
    }

    [Fact] public async Task response_then_events_invoke_with_return()
    {
        await GivenAggregate();

        var (tracked, response) = await theHost.InvokeMessageAndWaitAsync<Response>(new RaiseAABCC(theStreamId));
        response.ACount.ShouldBe(2);
        response.BCount.ShouldBe(1);
        response.CCount.ShouldBe(2);

        await OnAggregate(a =>
        {
            a.ACount.ShouldBe(2);
            a.BCount.ShouldBe(1);
            a.CCount.ShouldBe(2);
        });
    }

    [Fact]
    public async Task return_mix_of_events_messages_and_response()
    {
        await GivenAggregate();

        var (tracked, response) = await theHost.InvokeMessageAndWaitAsync<Response>(new RaiseBBCCC(theStreamId));

        // Just proves that this is what comes out of the handler
        response.ACount.ShouldBe(5);

        await OnAggregate(a =>
        {
            a.ACount.ShouldBe(0);
            a.BCount.ShouldBe(2);
            a.CCount.ShouldBe(3);
        });

        tracked.Sent.SingleMessage<LetterMessage1>().ShouldNotBeNull();
        tracked.Sent.SingleMessage<LetterMessage2>().ShouldNotBeNull();
    }

    [Fact]
    public async Task use_event_stream_arg_but_still_return_response()
    {
        await GivenAggregate();

        var (tracked, response) = await theHost.InvokeMessageAndWaitAsync<Response>(new RaiseAAA(theStreamId));
        response.CCount.ShouldBe(11);

        await OnAggregate(a =>
        {
            a.ACount.ShouldBe(3);
        });
    }

    [Fact]
    public async Task return_one_event_as_only_return_value()
    {
        await GivenAggregate();
        var tracked = await theHost.InvokeMessageAndWaitAsync(new RaiseOnlyD(theStreamId));
        tracked.Sent.MessagesOf<DEvent>().Any().ShouldBeFalse();
        tracked.Sent.MessagesOf<LetterAggregate>().Any().ShouldBeFalse();

        await OnAggregate(a => a.DCount.ShouldBe(1));
    }

    [Fact]
    public async Task append_events_from_async_enumerable()
    {
        await GivenAggregate();
        var tracked = await theHost.InvokeMessageAndWaitAsync(new RaiseLotsAsync(theStreamId));

        await OnAggregate(a =>
        {
            a.ACount.ShouldBe(4);
            a.BCount.ShouldBe(2);
            a.CCount.ShouldBe(3);
            a.DCount.ShouldBe(0);
        });
    }

    [Fact]
    public async Task if_only_returning_outgoing_messages_no_events()
    {
        var streamId = Guid.NewGuid();
        using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream<Aggregate>(streamId, new AEvent(), new BEvent());
            await session.SaveChangesAsync();
        }

        var tracked = await theHost.SendMessageAndWaitAsync(new Event3(streamId));

        var outgoing = tracked.FindSingleTrackedMessageOfType<Outgoing1>();
        outgoing.Aggregate.Id.ShouldBe(streamId);

        using (var session = theStore.LightweightSession())
        {
            var events = await session.Events.FetchStreamAsync(streamId);
            events.OfType<IEvent<OutgoingMessages>>().Any().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task using_updated_aggregate_as_response()
    {
        var streamId = Guid.NewGuid();
        using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream<Aggregate>(streamId, new AEvent(), new BEvent());
            await session.SaveChangesAsync();
        }

        var (tracked, updated) 
            = await theHost.InvokeMessageAndWaitAsync<LetterAggregate>(new Raise(streamId, 2, 3));
        
        tracked.Sent.AllMessages().ShouldBeEmpty();
        
        updated.ACount.ShouldBe(3);
        updated.BCount.ShouldBe(4);
    }

    [Fact]
    public async Task using_the_aggregate_in_a_before_method()
    {
        var streamId = Guid.NewGuid();
        var streamId2 = Guid.NewGuid();
        using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream<Aggregate>(streamId, new AEvent(), new CEvent());
            session.Events.StartStream<Aggregate>(streamId2, new CEvent(), new CEvent());
            await session.SaveChangesAsync();
        }
        
        await theHost.InvokeMessageAndWaitAsync(new RaiseIfValidated(streamId));
        await theHost.InvokeMessageAndWaitAsync(new RaiseIfValidated(streamId2));
        
        using (var session = theStore.LightweightSession())
        {
            // Should not apply anything new if there is a value for ACount
            var existing1 = await session.LoadAsync<LetterAggregate>(streamId);
            existing1.BCount.ShouldBe(0);
            
            // Should apply anything new if there was no value for ACount
            var existing2 = await session.LoadAsync<LetterAggregate>(streamId2);
            existing2.BCount.ShouldBe(1);
        }
    }
}

public record Event1(Guid AggregateId);
public record Event2(Guid AggregateId);
public record Event3(Guid AggregateId);

public class Aggregate
{
    public Guid Id { get; set; }

    public int Count { get; set; }

    public void Apply(AEvent e) => Count++;
    public void Apply(BEvent e) => Count++;
    public void Apply(CEvent e) => Count++;
}

[AggregateHandler]
public static class FooHandler
{
    // AppendMany events to aggregate.
    public static Events Handle(Event1 ev, Aggregate agg)
        => [];

    // AppendMany events to aggregate, cascaded messages.
    public static (Events, OutgoingMessages) Handle(Event2 ev, Aggregate agg)
        => ([], []);

    // BUG: AppendOne messages to aggregate.
    public static OutgoingMessages Handle(Event3 ev, Aggregate agg) =>
        [new Outgoing1 {Event = ev, Aggregate = agg}];
}

public static class Outgoing1Handler
{
    public static void Handle(Outgoing1 outgoing1) => Debug.WriteLine("Got an outgoing message");
}

public record Outgoing1
{
    public Event3 Event { get; set; }
    public Aggregate Aggregate { get; set; }
}

public record LetterMessage1;
public record LetterMessage2;

public static class ResponseHandler
{
    public static void Handle(Response cmd) => Debug.WriteLine("Got a response");
    public static void Handle(LetterMessage1 cmd) => Debug.WriteLine("Got a response");
    public static void Handle(LetterMessage2 cmd) => Debug.WriteLine("Got a response");
}

[AggregateHandler]
public static class RaiseLetterHandler
{

    public static (object[], Response) Handle(RaiseABC command, LetterAggregate aggregate)
    {
        aggregate.ACount++;
        aggregate.BCount++;
        aggregate.CCount++;
        return ([new AEvent(), new BEvent(), new CEvent()], Response.For(aggregate));
    }

    public static (Response, Events) Handle(RaiseAABCC command, LetterAggregate aggregate)
    {
        aggregate.ACount += 2;
        aggregate.BCount++;
        aggregate.CCount += 2;

        return (Response.For(aggregate), [new AEvent(), new AEvent(), new BEvent(), new CEvent(), new CEvent()]);
    }

    public static (Response, Events, OutgoingMessages) Handle(RaiseBBCCC command, LetterAggregate aggregate)
    {
        var events = new Events { new BEvent(), new BEvent(), new CEvent(), new CEvent(), new CEvent() };
        var messages = new OutgoingMessages { new LetterMessage1(), new LetterMessage2() };

        return (new Response { ACount = 5 }, events, messages);
    }

    public static Response Handle(RaiseAAA command, IEventStream<LetterAggregate> stream)
    {
        stream.AppendOne(new AEvent());
        stream.AppendOne(new AEvent());
        stream.AppendOne(new AEvent());
        return new Response { CCount = 11 };
    }

    public static DEvent Handle(RaiseOnlyD command, LetterAggregate aggregate) => new DEvent();

    public static async IAsyncEnumerable<object> Handle(RaiseLotsAsync command, LetterAggregate aggregate)
    {
        yield return new AEvent();
        yield return new AEvent();
        yield return new AEvent();
        yield return new AEvent();
        yield return new BEvent();
        await Task.Delay(25.Milliseconds());
        yield return new BEvent();
        yield return new CEvent();
        yield return new CEvent();
        yield return new CEvent();
    }

    public static (UpdatedAggregate, Events) Handle(Raise command, LetterAggregate aggregate)
    {
        var events = new Events();
        for (int i = 0; i < command.A; i++)
        {
            events.Add(new AEvent());
        }
        
        for (int i = 0; i < command.B; i++)
        {
            events.Add(new BEvent());
        }

        return (new UpdatedAggregate(), events);
    }
}

public record Raise(Guid LetterAggregateId, int A, int B);

public record RaiseLotsAsync(Guid LetterAggregateId);

public record RaiseOnlyD(Guid LetterAggregateId);

public record RaiseABC(Guid LetterAggregateId);
public record RaiseAAA(Guid LetterAggregateId);
public record RaiseAABCC(Guid LetterAggregateId);

public record RaiseBBCCC(Guid LetterAggregateId);

#region sample_passing_aggregate_into_validate_method

public record RaiseIfValidated(Guid LetterAggregateId);

public static class RaiseIfValidatedHandler
{
    public static HandlerContinuation Validate(LetterAggregate aggregate) =>
        aggregate.ACount == 0 ? HandlerContinuation.Continue : HandlerContinuation.Stop;
    
    [AggregateHandler]
    public static IEnumerable<object> Handle(RaiseIfValidated command, LetterAggregate aggregate)
    {
        yield return new BEvent();
    }
}

#endregion

public class Response
{
    public static Response For(LetterAggregate aggregate)
    {
        return new Response
        {
            ACount = aggregate.ACount,
            BCount = aggregate.BCount,
            CCount = aggregate.CCount,
            DCount = aggregate.DCount

        };
    }

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
}