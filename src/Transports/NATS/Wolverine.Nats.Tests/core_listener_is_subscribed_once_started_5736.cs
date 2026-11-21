using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Nats.Tests.Helpers;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-5736. Core NATS delivers a message only to the subscriptions the server knows about when it is published,
/// and there is no acknowledgement of a SUB: writing it to the connection is not the server having processed it.
/// A core listener that reported itself started as soon as its SUB was written left a window in which a message
/// published by another connection was dropped without a trace. The transport compliance suites hit it as a
/// timeout on whichever test sent first after the receiver started (and as "no responders" on a request).
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class core_listener_is_subscribed_once_started_5736
{
    private readonly NatsContainerFixture _fixture;

    public core_listener_is_subscribed_once_started_5736(NatsContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_message_sent_as_soon_as_the_receiver_has_started_is_received(bool inline)
    {
        var subject = $"gh5736.{Guid.NewGuid():N}";
        SubscribedOnStartHandler.Reset();

        // Everything the receiver writes reaches the server a second late. A listener that is only subscribed
        // client-side when the host finishes starting misses the message sent right after it, every time
        await using var proxy = new DelayingTcpProxy(_fixture.ConnectionString, 1.Seconds());
        proxy.Start();

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Gh5736Sender";
                opts.UseNats(_fixture.ConnectionString);
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<SubscribedOnStart>().ToNatsSubject(subject);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Gh5736Receiver";
                opts.UseNats(proxy.Url);

                var listener = opts.ListenToNatsSubject(subject);
                if (inline)
                {
                    listener.ProcessInline();
                }
                else
                {
                    listener.BufferedInMemory();
                }
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var message = new SubscribedOnStart(Guid.NewGuid());
        await sender.MessageBus().PublishAsync(message);

        var received = await SubscribedOnStartHandler.Received.Task.WaitAsync(10.Seconds(),
            TestContext.Current.CancellationToken);

        received.ShouldBe(message.Id);
    }
}

public record SubscribedOnStart(Guid Id);

public static class SubscribedOnStartHandler
{
    public static TaskCompletionSource<Guid> Received { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        Received = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Handle(SubscribedOnStart message)
    {
        Received.TrySetResult(message.Id);
    }
}
