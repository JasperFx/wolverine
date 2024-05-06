using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using TestingSupport.Compliance;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Acceptance;

public class sending_messages_to_named_endpoints : IDisposable
{
    private readonly IHost _receiver1;
    private readonly IHost _receiver2;
    private readonly IHost _receiver3;
    private readonly IHost _sender;

    public sending_messages_to_named_endpoints()
    {
        var port1 = PortFinder.GetAvailablePort();
        var port2 = PortFinder.GetAvailablePort();
        var port3 = PortFinder.GetAvailablePort();

        _sender = WolverineHost.For(opts =>
        {
            opts.Publish().ToPort(port1).Named("one");
            opts.Publish().ToPort(port2).Named("two");
            opts.Publish().ToPort(port3).Named("three");
        });

        _receiver1 = WolverineHost.For(opts =>
        {
            opts.ListenAtPort(port1);
            opts.ServiceName = "one";
        });

        _receiver2 = WolverineHost.For(opts =>
        {
            opts.ListenAtPort(port2);
            opts.ServiceName = "two";
        });

        _receiver3 = WolverineHost.For(opts =>
        {
            opts.ListenAtPort(port3);
            opts.ServiceName = "three";
        });
    }

    public void Dispose()
    {
        _sender?.Dispose();
        _receiver1?.Dispose();
        _receiver2?.Dispose();
        _receiver3?.Dispose();
    }

    [Fact]
    public async Task blows_up_with_descriptive_exception_when_trying_to_send_to_an_endpoint_that_does_not_exist()
    {
        await Should.ThrowAsync<UnknownEndpointException>(async () =>
        {
            await _sender.SendToEndpointAsync("nonexistent", new Message1());
        });
    }

    [Fact]
    public async Task blow_up_with_descriptive_exception_when_executing_message_with_cascading_message_to_unknown_endpoint()
    {
        await Should.ThrowAsync<UnknownEndpointException>(async () =>
        {
            await _sender.InvokeAsync(new TriggerRoutedMessage("nonexistent"));
        });
    }

    [Fact]
    public async Task send_to_a_specific_endpoint()
    {
        var session = await _sender.TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(_receiver1, _receiver2, _receiver3)
            .ExecuteAndWaitAsync(c => c.EndpointFor("two").SendAsync( new TrackedMessage()));

        session.FindEnvelopesWithMessageType<TrackedMessage>()
            .Single(x => x.MessageEventType == MessageEventType.Received)
            .ServiceName.ShouldBe("two");
    }

}

public class TrackedMessage;

public record TriggerRoutedMessage(string EndpointName);

public class TrackedMessageHandler
{
    public void Handle(TrackedMessage message)
    {
    }

    public object Handle(TriggerRoutedMessage message)
    {
        return new TrackedMessage().ToEndpoint("nonexistent");
    }
}