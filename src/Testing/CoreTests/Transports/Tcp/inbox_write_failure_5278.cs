using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports.Tcp;

/// <summary>
/// GH-5278. When a durable TCP listener could not write a received batch to its inbox, the durable receiver
/// "deferred" each envelope back to the SocketListener, whose DeferAsync does nothing, and the wire protocol
/// answered the sender with Received as soon as ReceivedAsync returned. The sender then deleted the envelopes
/// from its outbox, and the messages ended up in no table on either side. A broker redelivers a deferred message;
/// over TCP only the sender can, and only if it is not told the batch was received.
/// </summary>
public class inbox_write_failure_5278
{
    private static async Task<IHost> hostWith(InboxOutageMessageStore store, int port,
        Action<WolverineOptions>? configure = null)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TcpInboxPingHandler));
                opts.Services.AddSingleton<IMessageStore>(store);

                opts.ListenAtPort(port).UseDurableInbox();

                configure?.Invoke(opts);
            }).StartAsync();
    }

    private static async Task<string> sendBatchAsync(int port, string name)
    {
        var serializer = new SystemTextJsonSerializer(new JsonSerializerOptions());
        var envelope = new Envelope(new TcpInboxPing(name))
        {
            Serializer = serializer,
            ContentType = serializer.ContentType,
            Destination = $"tcp://localhost:{port}".ToUri()
        };

        var callback = new RecordingSenderCallback();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("localhost", port);
            await using var stream = client.GetStream();

            await WireProtocol.SendAsync(stream, new OutgoingMessageBatch(envelope.Destination, [envelope]), null,
                callback).WaitAsync(30.Seconds());
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            // The connection dropping is a failure the sender retries, which is as good as a refusal here
            return "connection dropped";
        }

        return callback.Outcome ?? "no answer";
    }

    private static async Task<bool> eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
        return condition();
    }

    [Fact]
    public async Task a_batch_the_inbox_could_not_store_is_not_answered_with_received()
    {
        var store = new InboxOutageMessageStore();
        var port = PortFinder.GetAvailablePort();
        using var host = await hostWith(store, port);

        // Control: with the inbox healthy the batch is received and handled
        (await sendBatchAsync(port, "tcp-control")).ShouldBe(nameof(ISenderCallback.MarkSuccessfulAsync));
        (await eventually(() => TcpInboxPingHandler.Handled.Contains("tcp-control"), 15.Seconds())).ShouldBeTrue();

        store.InboxIsDown = true;

        var outcome = await sendBatchAsync(port, "tcp-during-outage");

        store.FailedWrites.ShouldBeGreaterThan(0);
        outcome.ShouldNotBe(nameof(ISenderCallback.MarkSuccessfulAsync),
            "The listener told the sender the batch was received, but the inbox never stored it");
    }

    [Fact]
    public async Task a_durable_outbox_retries_a_batch_the_receiver_could_not_store_until_it_is_handled()
    {
        // The shape from the issue: a durable outbox sending over TCP to a durable listener
        var store = new InboxOutageMessageStore();
        var port = PortFinder.GetAvailablePort();
        using var host = await hostWith(store, port,
            opts => opts.PublishMessage<TcpInboxPing>().ToPort(port).UseDurableOutbox());

        await host.MessageBus().PublishAsync(new TcpInboxPing("tcp-outbox-control"));
        (await eventually(() => TcpInboxPingHandler.Handled.Contains("tcp-outbox-control"), 15.Seconds()))
            .ShouldBeTrue();

        store.InboxIsDown = true;
        try
        {
            await host.MessageBus().PublishAsync(new TcpInboxPing("tcp-outbox-during-outage"));
            await eventually(() => store.FailedWrites > 0, 15.Seconds());
            await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);
        }
        finally
        {
            store.InboxIsDown = false;
        }

        store.FailedWrites.ShouldBeGreaterThan(0);
        (await eventually(() => TcpInboxPingHandler.Handled.Contains("tcp-outbox-during-outage"), 60.Seconds()))
            .ShouldBeTrue("The message published while the receiver's inbox was down was never handled");
    }

    private class RecordingSenderCallback : ISenderCallback
    {
        public string? Outcome { get; private set; }

        private Task record(string outcome)
        {
            Outcome ??= outcome;
            return Task.CompletedTask;
        }

        public Task MarkSuccessfulAsync(OutgoingMessageBatch outgoing) => record(nameof(MarkSuccessfulAsync));
        public Task MarkTimedOutAsync(OutgoingMessageBatch outgoing) => record(nameof(MarkTimedOutAsync));

        public Task MarkSerializationFailureAsync(OutgoingMessageBatch outgoing) =>
            record(nameof(MarkSerializationFailureAsync));

        public Task MarkQueueDoesNotExistAsync(OutgoingMessageBatch outgoing) =>
            record(nameof(MarkQueueDoesNotExistAsync));

        public Task MarkProcessingFailureAsync(OutgoingMessageBatch outgoing) =>
            record(nameof(MarkProcessingFailureAsync));

        public Task MarkProcessingFailureAsync(OutgoingMessageBatch outgoing, Exception? exception) =>
            record(nameof(MarkProcessingFailureAsync));

        public Task MarkSenderIsLatchedAsync(OutgoingMessageBatch outgoing) =>
            record(nameof(MarkSenderIsLatchedAsync));
    }
}

public record TcpInboxPing(string Name);

public static class TcpInboxPingHandler
{
    public static readonly ConcurrentQueue<string> Handled = new();

    public static void Handle(TcpInboxPing ping) => Handled.Enqueue(ping.Name);
}
