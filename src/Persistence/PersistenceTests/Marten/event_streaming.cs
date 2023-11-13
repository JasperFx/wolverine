using IntegrationTests;
using JasperFx.Core;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
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
                opts.PublishAllMessages().ToPort(receiverPort).UseDurableOutbox();
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

    public void Dispose()
    {
        theReceiver?.Dispose();
        theSender?.Dispose();
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
    }
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
    
    public void Handle(IEvent<ThirdEvent> e)
    {
        
    }
}

public record SecondMessage(long Sequence);

public class SecondEvent
{
    
}

public class ThirdEvent{}

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
    
    public void Handle(SecondMessage message){}
}