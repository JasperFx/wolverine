using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// Reproduces https://github.com/JasperFx/wolverine/issues/2360
/// When using PublishAsync with DeliveryOptions.RequireResponse, the response message
/// should be handled by a normal message handler, NOT treated as a synchronous reply.
/// </summary>
public class Bug_2360_publish_with_require_response
{
    private readonly ITestOutputHelper _output;

    public Bug_2360_publish_with_require_response(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task publish_with_require_response_should_invoke_handler()
    {
        Bug2360ResponseHandler.WasHandled = false;

        var senderQueue = $"bug2360_sender_{Guid.NewGuid():N}";
        var receiverQueue = $"bug2360_receiver_{Guid.NewGuid():N}";

        // The "client" service that publishes a request and has a handler for the response
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Bug2360Sender";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();

                opts.PublishMessage<Bug2360Request>().ToRabbitQueue(receiverQueue);
                opts.ListenToRabbitQueue(senderQueue);

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Bug2360InitHandler))
                    .IncludeType(typeof(Bug2360ResponseHandler));
            }).StartAsync();

        // The "server" service that handles the request and returns a response
        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Bug2360Receiver";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();

                opts.ListenToRabbitQueue(receiverQueue);

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Bug2360RequestHandler));
            }).StartAsync();

        // Send the initial message that triggers PublishAsync with RequireResponse
        var session = await sender
            .TrackActivity()
            .AlsoTrack(receiver)
            .Timeout(30.Seconds())
            .IncludeExternalTransports()
            .InvokeMessageAndWaitAsync(new Bug2360Init());

        // The response handler should have been invoked — not just processed by the reply tracker
        Bug2360ResponseHandler.WasHandled.ShouldBeTrue(
            "Bug2360Response should have been handled by Bug2360ResponseHandler, " +
            "not consumed by the reply tracker. When PublishAsync is used with " +
            "RequireResponse, the response should be routed to a normal handler.");
    }

    [Fact]
    public async Task invoke_async_still_works_for_request_reply()
    {
        var receiverQueue = $"bug2360_invoke_{Guid.NewGuid():N}";

        // The "client" service that uses InvokeAsync for synchronous request/reply
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Bug2360InvokeSender";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();

                opts.PublishMessage<Bug2360Request>().ToRabbitQueue(receiverQueue);

                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();

        // The "server" service
        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Bug2360InvokeReceiver";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();

                opts.ListenToRabbitQueue(receiverQueue);

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Bug2360RequestHandler));
            }).StartAsync();

        // InvokeAsync should still work as a synchronous request/reply
        var bus = sender.MessageBus();
        var response = await bus.InvokeAsync<Bug2360Response>(new Bug2360Request("InvokeTest"), timeout: 30.Seconds());

        response.ShouldNotBeNull();
        response.Reply.ShouldBe("Handled: InvokeTest");
    }
}

// Messages
public record Bug2360Init;
public record Bug2360Request(string Text);
public record Bug2360Response(string Reply);

// Client-side handlers
public static class Bug2360InitHandler
{
    // This handler publishes a request with RequireResponse - the response should
    // be handled by Bug2360ResponseHandler, not by Wolverine's internal reply tracker
    public static ValueTask Handle(Bug2360Init message, IMessageContext context)
    {
        return context.PublishAsync(new Bug2360Request("Hello"),
            DeliveryOptions.RequireResponse<Bug2360Response>());
    }
}

public static class Bug2360ResponseHandler
{
    public static bool WasHandled;

    public static void Handle(Bug2360Response response)
    {
        WasHandled = true;
    }
}

// Server-side handler
public static class Bug2360RequestHandler
{
    public static Bug2360Response Handle(Bug2360Request request)
    {
        return new Bug2360Response($"Handled: {request.Text}");
    }
}
