using System.Diagnostics;
using IntegrationTests;
using JasperFx.CodeGeneration;
using Marten;
using Marten.Events;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.Marten;

public class advanced_command_workflow_application: PostgresqlContext, IAsyncLifetime
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
                        m.Projections.SelfAggregate<LetterAggregate>(ProjectionLifecycle.Inline);
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine()
                    .ApplyAllDatabaseChangesOnStartup();

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();
        
        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
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
        
        tracked.Sent.SingleMessage<Response>().ACount.ShouldBe(1);

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
        
        tracked.Sent.SingleMessage<Response>().ACount.ShouldBe(2);

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
        return (new object[] { new AEvent(), new BEvent(), new CEvent() }, Response.For(aggregate));
    }
    
    public static (Response, Events) Handle(RaiseAABCC command, LetterAggregate aggregate)
    {
        aggregate.ACount += 2;
        aggregate.BCount++;
        aggregate.CCount += 2;
        
        return (Response.For(aggregate), new Events{new AEvent(), new AEvent(), new BEvent(), new CEvent(), new CEvent()});
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
}

   
    
public record RaiseABC(Guid LetterAggregateId);
public record RaiseAAA(Guid LetterAggregateId);
public record RaiseAABCC(Guid LetterAggregateId);

public record RaiseBBCCC(Guid LetterAggregateId);

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