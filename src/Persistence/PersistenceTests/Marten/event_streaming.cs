using IntegrationTests;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Xunit;

namespace PersistenceTests.Marten;

public class event_streaming : PostgresqlContext, IAsyncLifetime
{
    private IHost theReceiver;
    private IHost theSender;

    public async Task InitializeAsync()
    {
        var receiverPort = PortFinder.GetAvailablePort();

        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(receiverPort);
                opts.Durability.Mode = DurabilityMode.Solo;
            })
            .ConfigureServices(services =>
            {
                services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine("receiver");

                services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theReceiver.ResetResourceState();

        theSender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Publish(x =>
                {
                    x.Message<TriggeredEvent>();
                    x.Message<SecondMessage>();

                    x.ToPort(receiverPort).UseDurableOutbox();
                });
                
                opts.DisableConventionalDiscovery().IncludeType<TriggerHandler>();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ServiceName = "sender";
            })
            .ConfigureServices(services =>
            {
                services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine("sender").EventForwardingToWolverine(opts =>
                    {
                        opts.SubscribeToEvent<SecondEvent>().TransformedTo(e => new SecondMessage(e.Sequence));
                    });
                
                services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theSender.ResetResourceState();
    }

    public async Task DisposeAsync()
    {
        await theReceiver.StopAsync();
        await theSender.StopAsync();
    }

    [Fact]
    public void  preview_routes()
    {
        var routes = theSender.GetRuntime().RoutingFor(typeof(IEvent<ThirdEvent>)).Routes;

        routes.Single().ShouldBeOfType<EventUnwrappingMessageRoute<ThirdEvent>>();
    }

    [Fact]
    public async Task event_should_be_published_from_sender_to_receiver()
    {
        var command = new TriggerCommand();

        var results = await theSender.TrackActivity().AlsoTrack(theReceiver).InvokeMessageAndWaitAsync(command);

        var triggered = results.Received.SingleMessage<TriggeredEvent>();
        triggered.ShouldNotBeNull();
        triggered.Id.ShouldBe(command.Id);
        
        results.Received.SingleMessage<SecondMessage>()
            .Sequence.ShouldBeGreaterThan(0);

        results.Executed.SingleMessage<ThirdEvent>().ShouldNotBeNull();
    }

    #region sample_execution_of_forwarded_events_can_be_awaited_from_tests
    [Fact]
    public async Task execution_of_forwarded_events_can_be_awaited_from_tests()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .ConfigureServices(services =>
            {
                services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine().EventForwardingToWolverine(opts =>
                    {
                        opts.SubscribeToEvent<SecondEvent>().TransformedTo(e => 
                            new SecondMessage(e.StreamId, e.Sequence));
                    });
            }).StartAsync();

        var aggregateId = Guid.NewGuid();
        await host.SaveInMartenAndWaitForOutgoingMessagesAsync(session =>
        {
            session.Events.Append(aggregateId, new SecondEvent());
        }, 100_000);
        
        using var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(aggregateId);
        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<SecondEvent>();
        events[1].Data.ShouldBeOfType<FourthEvent>();
    }
    #endregion
}


public class TriggerCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class TriggerHandler
{
    [Transactional]
    public void Handle(TriggerCommand command, IDocumentSession session)
    {
        session.Events.StartStream(command.Id, new TriggeredEvent { Id = command.Id }, new SecondEvent(), new ThirdEvent());
    }

    public void Handle(ThirdEvent e)
    {
    }
}

public record SecondMessage(Guid AggregateId, long Sequence);

public class SecondEvent
{
    
}

public class ThirdEvent{}
public class FourthEvent{}

public class TriggeredEvent
{
    public Guid Id { get; set; }
}

public class TriggerEventHandler
{
    private static readonly TaskCompletionSource<TriggeredEvent> _source = new();
    public static Task<TriggeredEvent> Waiter => _source.Task;

    public void Handle(TriggeredEvent message)
    {
        _source.SetResult(message);
    }

    #region sample_execution_of_forwarded_events_second_message_to_fourth_event
    public static Task HandleAsync(SecondMessage message, IDocumentSession session)
    {
        session.Events.Append(message.AggregateId, new FourthEvent());
        return session.SaveChangesAsync();
    }
    #endregion
}