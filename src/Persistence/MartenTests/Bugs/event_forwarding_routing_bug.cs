using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class event_forwarding_routing_bug
{
    [Fact]
    public async Task forwarded_events_respects_routing_rules()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToLocalQueue("forwarded-events");
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "forwarding_routing";
                    })
                    .IntegrateWithWolverine()
                    .EventForwardingToWolverine();
            })
            .StartAsync();

        var session = await host.SendMessageAndWaitAsync(new Event<SomeEvent>(new SomeEvent()));
        session.Executed.SingleEnvelope<IEvent<SomeEvent>>()
            .Destination.ShouldBe("local://forwarded-events/".ToUri());

        var bus = host.MessageBus();
        bus.PreviewSubscriptions(new Event<SomeEvent>(new SomeEvent()))
            .ShouldAllBe(x => x.Destination == new Uri("local://forwarded-events"));
    }

    [Fact]
    public async Task subscription_events_respects_routing_rules()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToLocalQueue("forwarded-events");
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "forwarding_routing";
                    })
                    .IntegrateWithWolverine()
                    .PublishEventsToWolverine("forwarded-events");
            })
            .StartAsync();

        var bus = host.MessageBus();
        bus.PreviewSubscriptions(new Event<SomeEvent>(new SomeEvent()))
            .ShouldAllBe(x => x.Destination == new Uri("local://forwarded-events"));
    }

    public record SomeEvent;

    public static class SomeEventHandler
    {
        public static void Handle(IEvent<SomeEvent> _)
        {
        }
    }
}