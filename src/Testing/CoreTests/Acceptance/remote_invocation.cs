using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Attributes;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Acceptance;

public class remote_invocation : IAsyncLifetime
{
    private IHost _receiver1;

    private int _receiver1Port;

    private IHost _receiver2;

    private int _receiver2Port;

    private IHost _sender;

    public async Task InitializeAsync()
    {
        var senderPort = PortFinder.GetAvailablePort();
        _receiver1Port = PortFinder.GetAvailablePort();
        _receiver2Port = PortFinder.GetAvailablePort();

        _receiver1 = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Receiver1";
                opts.ListenAtPort(_receiver1Port);
            }).StartAsync();

        _receiver2 = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Receiver2";
                opts.ListenAtPort(_receiver2Port);
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery();
                opts.ServiceName = "Sender";
                opts.ListenAtPort(senderPort);

                opts.PublishMessage<Request1>().ToPort(_receiver1Port).Named("Receiver1");
                opts.PublishMessage<Request2>().ToPort(_receiver1Port).Named("Receiver1");
                opts.PublishMessage<Request3>().ToPort(_receiver2Port).Named("Receiver2");

                opts.PublishMessage<Request4>().ToPort(_receiver1Port);
                opts.PublishMessage<Request4>().ToPort(_receiver2Port);
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _receiver1.StopAsync();
        await _receiver2.StopAsync();
        await _sender.StopAsync();
    }

    /*

     * Pass CancellationToken through to ReplyListener
     */

    [Fact]
    public async Task request_reply_with_no_reply()
    {
        using var nested = _sender.Services.CreateScope();
        var publisher = nested.ServiceProvider.GetRequiredService<IMessageBus>();

        var ex = await Should.ThrowAsync<WolverineRequestReplyException>(async () =>
        {
            await publisher.InvokeAsync<Response1>(new Request3());
        });

        ex.Message.ShouldContain(
            "Request failed: No response was created for expected response 'CoreTests.Acceptance.Response1'");
    }

    [Fact]
    public async Task send_and_wait_with_multiple_subscriptions()
    {
        using var nested = _sender.Services.CreateScope();
        var publisher = nested.ServiceProvider.GetRequiredService<IMessageBus>();

        var ex = await Should.ThrowAsync<MultipleSubscribersException>(async () =>
        {
            await publisher.InvokeAsync(new Request4());
        });

    }

    [Fact]
    public async Task happy_path_with_auto_routing()
    {
        var (session, response) = await _sender.TrackActivity()
            .AlsoTrack(_receiver1, _receiver2)
            .Timeout(5.Seconds())
            .InvokeAndWaitAsync<Response1>(new Request1 { Name = "Croaker" });

        var send = session.FindEnvelopesWithMessageType<Request1>()
            .Single(x => x.MessageEventType == MessageEventType.Sent);

        send.Envelope.DeliverBy.ShouldNotBeNull();

        var envelope = session.Received.SingleEnvelope<Response1>();
        envelope.Source.ShouldBe("Receiver1");
        envelope.Message.ShouldBe(response);

        response.Name.ShouldBe("Croaker");
    }

    [Fact]
    public async Task sad_path_request_reply_no_subscriptions()
    {
        using var nested = _sender.Services.CreateScope();
        var publisher = nested.ServiceProvider.GetRequiredService<IMessageBus>();

        await Should.ThrowAsync<IndeterminateRoutesException>(async () =>
        {
            await publisher.InvokeAsync<Response1>(new RequestWithNoHandler());
        });
    }

    [Fact]
    public async Task happy_path_with_explicit_endpoint_name()
    {
        Response1 response = null!;

        Func<IMessageContext, Task<Response1>> fetch = async c =>
            response = await c.EndpointFor("Receiver2").InvokeAsync<Response1>(new Request1 { Name = "Croaker" });

        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver1, _receiver2)
            .Timeout(5.Seconds())
            .ExecuteAndWaitAsync(fetch);

        var send = session.FindEnvelopesWithMessageType<Request1>()
            .Single(x => x.MessageEventType == MessageEventType.Sent);

        send.Envelope.DeliverBy.ShouldNotBeNull();

        var envelope = session.Received.SingleEnvelope<Response1>();
        envelope.Source.ShouldBe("Receiver2");
        envelope.Message.ShouldBe(response);

        response.Name.ShouldBe("Croaker");
    }

    [Fact]
    public async Task happy_path_with_explicit_uri_destination()
    {
        var destination = new Uri("tcp://localhost:" + _receiver2Port);

        Response1 response = default;

        Func<IMessageContext, Task> fetch = async c =>
            response = await c.EndpointFor(destination).InvokeAsync<Response1>(new Request1 { Name = "Croaker" });

        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver1, _receiver2)
            .Timeout(5.Seconds())
            .ExecuteAndWaitAsync(fetch);

        var send = session.FindEnvelopesWithMessageType<Request1>()
            .Single(x => x.MessageEventType == MessageEventType.Sent);

        send.Envelope.DeliverBy.ShouldNotBeNull();

        var envelope = session.Received.SingleEnvelope<Response1>();
        envelope.Source.ShouldBe("Receiver2");
        envelope.Message.ShouldBe(response);

        response.Name.ShouldBe("Croaker");
    }

    [Fact]
    public async Task sad_path_with_auto_routing()
    {
        var ex = await Should.ThrowAsync<WolverineRequestReplyException>(async () =>
        {
            var (session, response) = await _sender.TrackActivity()
                .AlsoTrack(_receiver1, _receiver2)
                .Timeout(5.Seconds())
                .DoNotAssertOnExceptionsDetected()
                // This message is rigged to fail
                .InvokeAndWaitAsync<Response1>(new Request1 { Name = "Soulcatcher" });
        });

        ex.Message.ShouldContain("Request failed");
        ex.Message.ShouldContain("System.Exception: You shall not pass!");
    }

    [Fact]
    public async Task timeout_with_auto_routing()
    {
        using var nested = _sender.Services.CreateScope();
        var publisher = nested.ServiceProvider.GetRequiredService<IMessageBus>();

        var ex = await Should.ThrowAsync<TimeoutException>(async () =>
        {
            await publisher.InvokeAsync<Response1>(new Request1 { Name = "SLOW" });
        });

        ex.Message.ShouldContain("Timed out waiting for expected response CoreTests.Acceptance.Response1");
    }

    [Fact]
    public async Task happy_path_send_and_wait_with_auto_routing()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver1, _receiver2)
            .Timeout(5.Seconds())
            .InvokeMessageAndWaitAsync(new Request2 { Name = "Croaker" });

        var send = session.FindEnvelopesWithMessageType<Request2>()
            .Single(x => x.MessageEventType == MessageEventType.Sent);

        send.Envelope.DeliverBy.ShouldNotBeNull();

        var envelope = session.Received.SingleEnvelope<Acknowledgement>();
        envelope.Source.ShouldBe("Receiver1");
    }

    [Fact]
    public async Task happy_path_send_and_wait_to_specific_endpoint()
    {
        var (session, ack) = await _sender.TrackActivity()
            .AlsoTrack(_receiver1, _receiver2)
            .Timeout(5.Seconds())
            .SendMessageAndWaitForAcknowledgementAsync(c =>
                c.EndpointFor("Receiver2").InvokeAsync(new Request2 { Name = "Croaker" }));

        var send = session.FindEnvelopesWithMessageType<Request2>()
            .Single(x => x.MessageEventType == MessageEventType.Sent);

        send.Envelope.DeliverBy.ShouldNotBeNull();

        var envelope = session.Received.SingleEnvelope<Acknowledgement>();
        envelope.Source.ShouldBe("Receiver2");
    }

    [Fact]
    public async Task happy_path_send_and_wait_to_specific_endpoint_by_uri()
    {
        var destination = new Uri("tcp://localhost:" + _receiver2Port);

        var (session, ack) = await _sender.TrackActivity()
            .AlsoTrack(_receiver1, _receiver2)
            .Timeout(5.Seconds())
            .SendMessageAndWaitForAcknowledgementAsync(c =>
                c.EndpointFor(destination).InvokeAsync( new Request2 { Name = "Croaker" }));

        var send = session.FindEnvelopesWithMessageType<Request2>()
            .Single(x => x.MessageEventType == MessageEventType.Sent);

        send.Envelope.DeliverBy.ShouldNotBeNull();

        var envelope = session.Received.SingleEnvelope<Acknowledgement>();
        envelope.Source.ShouldBe("Receiver2");
    }

    [Fact]
    public async Task sad_path_send_and_wait_for_acknowledgement_with_auto_routing()
    {
        var ex = await Should.ThrowAsync<WolverineRequestReplyException>(async () =>
        {
            var session = await _sender.TrackActivity()
                .AlsoTrack(_receiver1, _receiver2)
                .Timeout(5.Seconds())
                .DoNotAssertOnExceptionsDetected()
                // This message is rigged to fail

                .InvokeMessageAndWaitAsync(new Request2 { Name = "Limper" });
        });

        ex.Message.ShouldContain("Request failed");
        ex.Message.ShouldContain("System.Exception: You shall not pass!");
    }

    [Fact]
    public async Task timeout_on_send_and_wait_with_auto_routing()
    {
        using var nested = _sender.Services.CreateScope();
        var publisher = nested.ServiceProvider.GetRequiredService<IMessageBus>();

        var ex = await Should.ThrowAsync<TimeoutException>(async () =>
        {
            await publisher.InvokeAsync(new Request2 { Name = "SLOW" });
        });

        ex.Message.ShouldContain("Timed out waiting for expected acknowledgement for original message");
    }

    [Fact]
    public async Task sad_path_request_and_reply_with_no_handler()
    {
        using var nested = _sender.Services.CreateScope();
        var publisher = nested.ServiceProvider.GetRequiredService<IMessageBus>();

        var ex = await Should.ThrowAsync<WolverineRequestReplyException>(async () =>
        {
            await publisher.EndpointFor("Receiver1").InvokeAsync<Response1>(new RequestWithNoHandler());
        });

        ex.Message.ShouldContain(
            "No known message handler for message type 'CoreTests.Acceptance.RequestWithNoHandler'");
    }

    [Fact]
    public async Task sad_path_send_and_wait_with_no_handler()
    {
        using var nested = _sender.Services.CreateScope();
        var publisher = nested.ServiceProvider.GetRequiredService<IMessageBus>();

        var ex = await Should.ThrowAsync<WolverineRequestReplyException>(async () =>
        {
            await publisher.EndpointFor("Receiver1").InvokeAsync( new RequestWithNoHandler());
        });

        ex.Message.ShouldContain(
            "No known message handler for message type 'CoreTests.Acceptance.RequestWithNoHandler'");
    }

    [Fact]
    public async Task sad_path_send_and_wait_with_no_subscription()
    {
        using var nested = _sender.Services.CreateScope();
        var publisher = nested.ServiceProvider.GetRequiredService<IMessageBus>();

        await Should.ThrowAsync<IndeterminateRoutesException>(() => publisher.InvokeAsync(new RequestWithNoHandler()));
    }
}

public class Request1
{
    public string Name { get; set; }
}

public class Request2
{
    public string Name { get; set; }
}

public class Request3
{
    public string Name { get; set; }
}

public class Request4
{
    public string Name { get; set; }
}

public class RequestWithNoHandler;

public class Response1
{
    public string Name { get; set; }
}

public class Response3
{
    public string Name { get; set; }
}

public class RequestHandler
{
    [MessageTimeout(3)]
    public async Task<Response1> Handle(Request1 request)
    {
        if (request.Name == "Soulcatcher")
        {
            throw new Exception("You shall not pass!");
        }

        if (request.Name == "SLOW")
        {
            await Task.Delay(5.Seconds());
        }

        return new Response1 { Name = request.Name };
    }

    [MessageTimeout(3)]
    public async Task Handle(Request2 request)
    {
        if (request.Name == "Limper")
        {
            throw new Exception("You shall not pass!");
        }

        if (request.Name == "SLOW")
        {
            await Task.Delay(6.Seconds());
        }
    }

    public Response3 Handle(Request3 request)
    {
        return new Response3 { Name = request.Name };
    }
}