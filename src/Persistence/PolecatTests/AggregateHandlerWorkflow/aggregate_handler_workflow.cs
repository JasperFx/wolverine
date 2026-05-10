using System.Diagnostics;
using JasperFx.Events.Projections;
using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Resources;
using Polecat;
using Polecat.Events;
using Polecat.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.AggregateHandlerWorkflow;

public class aggregate_handler_workflow : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private Guid theStreamId;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "agg_handler";
                        m.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Inline);
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)theStore).Database.ApplyAllConfiguredChangesToDatabaseAsync();
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
    public void automatically_adding_stream_id_to_the_audit_members()
    {
        var handler = theHost.GetRuntime().Handlers.HandlerFor<RaiseABC>();
        var chain = theHost.GetRuntime().Handlers.ChainFor<RaiseABC>();

        chain.AuditedMembers.Single().MemberName.ShouldBe(nameof(RaiseABC.LetterAggregateId));

        chain.SourceCode.ShouldContain("System.Diagnostics.Activity.Current?.SetTag(\"letter.aggregate.id\", raiseABC.LetterAggregateId);");
    }

    [Fact]
    public void generates_wolverine_stream_id_otel_tag()
    {
        // Resolving the handler triggers chain compilation; without this the
        // chain's generated SourceCode is null and the assertion below NREs.
        // Mirrors the equivalent Marten test (MartenTests/AggregateHandlerWorkflow).
        var handler = theHost.GetRuntime().Handlers.HandlerFor<RaiseABC>();
        var chain = theHost.GetRuntime().Handlers.ChainFor<RaiseABC>();

        chain!.SourceCode!.ShouldContain($"SetTag(\"{Wolverine.Runtime.WolverineTracing.StreamId}\"");
    }

    [Fact]
    public void generates_wolverine_stream_type_otel_tag()
    {
        var handler = theHost.GetRuntime().Handlers.HandlerFor<RaiseABC>();
        var chain = theHost.GetRuntime().Handlers.ChainFor<RaiseABC>();

        chain!.SourceCode!.ShouldContain($"SetTag(\"{Wolverine.Runtime.WolverineTracing.StreamType}\", \"{typeof(LetterAggregate).FullName}\"");
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

    [Fact]
    public async Task response_then_events_invoke_no_return()
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

    [Fact]
    public async Task response_then_events_invoke_with_return()
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
        await using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream<PcAggregate>(streamId, new AEvent(), new BEvent());
            await session.SaveChangesAsync();
        }

        var tracked = await theHost.SendMessageAndWaitAsync(new PcEvent3(streamId));

        var outgoing = tracked.FindSingleTrackedMessageOfType<PcOutgoing1>();
        outgoing.Aggregate.Id.ShouldBe(streamId);

        await using (var session = theStore.LightweightSession())
        {
            var events = await session.Events.FetchStreamAsync(streamId);
            events.OfType<IEvent<OutgoingMessages>>().Any().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task using_updated_aggregate_as_response()
    {
        var streamId = Guid.NewGuid();
        await using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream<PcAggregate>(streamId, new AEvent(), new BEvent());
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
        await using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream<PcAggregate>(streamId, new AEvent(), new CEvent());
            session.Events.StartStream<PcAggregate>(streamId2, new CEvent(), new CEvent());
            await session.SaveChangesAsync();
        }

        await theHost.InvokeMessageAndWaitAsync(new RaiseIfValidated(streamId));
        await theHost.InvokeMessageAndWaitAsync(new RaiseIfValidated(streamId2));

        await using (var session = theStore.LightweightSession())
        {
            var existing1 = await session.LoadAsync<LetterAggregate>(streamId);
            existing1.BCount.ShouldBe(0);

            var existing2 = await session.LoadAsync<LetterAggregate>(streamId2);
            existing2.BCount.ShouldBe(1);
        }
    }
}

public record PcEvent1(Guid PcAggregateId);
public record PcEvent2(Guid PcAggregateId);
public record PcEvent3(Guid PcAggregateId);

public class PcAggregate
{
    public Guid Id { get; set; }

    public int Count { get; set; }

    public void Apply(AEvent e) => Count++;
    public void Apply(BEvent e) => Count++;
    public void Apply(CEvent e) => Count++;
}

[AggregateHandler]
public static class PcFooHandler
{
    public static Events Handle(PcEvent1 ev, PcAggregate agg)
        => [];

    public static (Events, OutgoingMessages) Handle(PcEvent2 ev, PcAggregate agg)
        => ([], []);

    public static OutgoingMessages Handle(PcEvent3 ev, PcAggregate agg) =>
        [new PcOutgoing1 { Event = ev, Aggregate = agg }];
}

public static class PcOutgoing1Handler
{
    public static void Handle(PcOutgoing1 outgoing1) => Debug.WriteLine("Got an outgoing message");
}

public record PcOutgoing1
{
    public required PcEvent3 Event { get; init; }
    public required PcAggregate Aggregate { get; init; }
}

public record LetterMessage1;
public record LetterMessage2;

public static class PcResponseHandler
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
