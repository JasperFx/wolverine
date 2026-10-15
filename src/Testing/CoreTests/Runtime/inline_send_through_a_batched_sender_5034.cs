using System.Net;
using System.Net.Sockets;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-5034. Under ServerlessWithOutbox every sending endpoint is durable, and on SQS, SNS, Azure Service Bus,
/// Pub/Sub, Kafka, Redis, MQTT, TCP and HTTP that means a BatchedSender -- whose SendAsync() only queues the
/// envelope in memory. Neither the inline send nor the durability pass may delete an outbox row until the
/// broker has actually accepted the message. TCP is the BatchedSender transport in core, so it stands in
/// for all of them here
/// </summary>
public class inline_send_through_a_batched_sender_5034 : IAsyncDisposable
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly List<IAsyncDisposable> _disposables = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }

    private DurableSendingAgent durableTcpAgent(int port)
    {
        var endpoint = new TcpEndpoint(port);
        var sender = new BatchedSender(endpoint, new SocketSenderProtocol(), CancellationToken.None,
            NullLogger.Instance);

        var agent = new DurableSendingAgent(sender, theRuntime.Options.Durability, NullLogger.Instance,
            Substitute.For<IMessageTracker>(), Substitute.For<IMessageOutbox>(), endpoint);
        _disposables.Add(agent);

        theRuntime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri).Returns(agent);

        return agent;
    }

    private static Envelope envelopeTo(ISendingAgent agent)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = agent.Destination;
        envelope.Sender = agent;
        envelope.DeliverBy = null;
        return envelope;
    }

    // Nothing listens on it, so a connection to it is refused
    private static int deadPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<MessageContext> contextWithOutgoing(params Envelope[] envelopes)
    {
        var context = new MessageContext(theRuntime);

        // The handler's transaction (Marten, EF Core...) has already persisted the durable envelopes
        await context.EnlistInOutboxAsync(Substitute.For<IEnvelopeTransaction>());
        await context.As<IEnvelopeTransaction>().PersistOutgoingAsync(envelopes);

        return context;
    }

    [Fact]
    public async Task the_inline_send_leaves_the_outbox_row_when_the_broker_never_got_the_message()
    {
        var agent = durableTcpAgent(deadPort());
        var envelope = envelopeTo(agent);
        var context = await contextWithOutgoing(envelope);

        await context.SendAllOutgoingMessagesInlineAsync();

        // The BatchedSender would have queued it and "succeeded", and the row would be gone with the message
        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
        theRuntime.MessageTracking.DidNotReceive().Sent(envelope);
    }

    [Fact]
    public async Task the_inline_send_only_deletes_the_outbox_row_once_the_broker_has_the_message()
    {
        await using var broker = new FakeTcpBroker();
        var agent = durableTcpAgent(broker.Port);
        var envelope = envelopeTo(agent);
        var context = await contextWithOutgoing(envelope);

        await context.SendAllOutgoingMessagesInlineAsync();

        // Already on the wire and confirmed by the time the request could be frozen
        broker.Received.ShouldBe(1);

        await theRuntime.Storage.Outbox.Received(1)
            .DeleteOutgoingAsync(Arg.Is<Envelope[]>(x => x.Length == 1 && x[0] == envelope));
    }

    [Fact]
    public async Task the_durability_pass_leaves_the_outbox_row_when_the_broker_never_got_the_message()
    {
        var agent = durableTcpAgent(deadPort());
        var envelope = envelopeTo(agent);

        var report = await recoverAsync(envelope);

        report.OutgoingSent.ShouldBe(0);
        report.OutgoingFailed.ShouldBe(1);
        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
    }

    [Fact]
    public async Task the_durability_pass_only_deletes_the_outbox_row_once_the_broker_has_the_message()
    {
        await using var broker = new FakeTcpBroker();
        var agent = durableTcpAgent(broker.Port);
        var envelope = envelopeTo(agent);

        var report = await recoverAsync(envelope);

        report.OutgoingSent.ShouldBe(1);
        broker.Received.ShouldBe(1);
        await theRuntime.Storage.Outbox.Received(1)
            .DeleteOutgoingAsync(Arg.Is<Envelope[]>(x => x.Length == 1 && x[0] == envelope));
    }

    private Task<DurabilityPassStepResult> recoverAsync(Envelope envelope)
    {
        // A recovered row has no sending agent of its own; the step finds it by destination
        envelope.Sender = null;

        var source = Substitute.For<IStaleOutgoingSource>();
        source.LoadStaleOutgoingAsync(default, default!, default, default)
            .ReturnsForAnyArgs(new List<Envelope> { envelope });

        var step = new RecoverStaleOutgoingStep(theRuntime.Storage, source, NullLogger.Instance);
        var context = new DurabilityPassContext(theRuntime, theRuntime.Storage, 10,
            DateTimeOffset.UtcNow.AddMinutes(1), () => DateTimeOffset.UtcNow);

        return step.ExecuteAsync(context, CancellationToken.None);
    }

    /// <summary>
    /// Just enough of Wolverine's TCP wire protocol to accept batches and confirm them
    /// </summary>
    private class FakeTcpBroker : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _accepting;
        private int _received;

        public FakeTcpBroker()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = Task.Run(acceptAsync);
        }

        public int Port { get; }

        public int Received => _received;

        private async Task acceptAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                await using var stream = client.GetStream();

                var length = BitConverter.ToInt32(await readAsync(stream, 4));
                await readAsync(stream, length);
                Interlocked.Increment(ref _received);

                await stream.WriteAsync(WireProtocol.ReceivedBuffer, _cancellation.Token);
                await readAsync(stream, WireProtocol.AcknowledgedBuffer.Length);
            }
        }

        private async Task<byte[]> readAsync(Stream stream, int count)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, _cancellation.Token);
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();

            try
            {
                await _accepting.WaitAsync(5.Seconds());
            }
            catch (Exception)
            {
                // Shutting down
            }
        }
    }
}
