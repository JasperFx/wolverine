using System.Net;
using System.Net.Sockets;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
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

    public inline_send_through_a_batched_sender_5034()
    {
        // GH-5038: the inline send's own failure handling, which is the ServerlessWithOutbox one. Outside that
        // mode a failed send goes to the destination's sending agent instead
        theRuntime.Options.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }

    private DurableSendingAgent durableTcpAgent(int port, int? messageBatchSize = null)
    {
        var endpoint = new TcpEndpoint(port);
        if (messageBatchSize.HasValue) endpoint.MessageBatchSize = messageBatchSize.Value;

        return durableAgent(endpoint, new BatchedSender(endpoint, new SocketSenderProtocol(), CancellationToken.None,
            NullLogger.Instance));
    }

    private DurableSendingAgent durableAgent(Endpoint endpoint, ISender sender)
    {
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

    [Fact]
    public async Task the_inline_send_puts_every_envelope_for_one_destination_in_one_round_trip()
    {
        await using var broker = new FakeTcpBroker();
        var agent = durableTcpAgent(broker.Port);
        var envelopes = new[] { envelopeTo(agent), envelopeTo(agent), envelopeTo(agent) };
        var context = await contextWithOutgoing(envelopes);

        await context.SendAllOutgoingMessagesInlineAsync();

        broker.Received.ShouldBe(1);
        await theRuntime.Storage.Outbox.Received(1)
            .DeleteOutgoingAsync(Arg.Is<Envelope[]>(x => x.Length == 3 && envelopes.All(x.Contains)));
    }

    [Fact]
    public async Task the_inline_send_batches_by_the_endpoints_message_batch_size()
    {
        await using var broker = new FakeTcpBroker();
        var agent = durableTcpAgent(broker.Port, messageBatchSize: 2);
        var envelopes = Enumerable.Range(0, 5).Select(_ => envelopeTo(agent)).ToArray();
        var context = await contextWithOutgoing(envelopes);

        await context.SendAllOutgoingMessagesInlineAsync();

        broker.Received.ShouldBe(3);
        await theRuntime.Storage.Outbox.Received(1)
            .DeleteOutgoingAsync(Arg.Is<Envelope[]>(x => x.Length == 5));
    }

    [Fact]
    public async Task the_inline_send_only_deletes_what_the_broker_accepted_from_a_partly_failed_batch()
    {
        var endpoint = new TcpEndpoint(deadPort());
        var protocol = new PartlyAcceptingProtocol();
        var agent = durableAgent(endpoint, new BatchedSender(endpoint, protocol, CancellationToken.None, NullLogger.Instance));

        var accepted = envelopeTo(agent);
        var rejected = envelopeTo(agent);
        var ignored = envelopeTo(agent);
        protocol.Rejected.Add(rejected.Id);
        protocol.Ignored.Add(ignored.Id);

        var context = await contextWithOutgoing(accepted, rejected, ignored);
        await context.SendAllOutgoingMessagesInlineAsync();

        // One round trip, and a protocol that says nothing about an envelope did not send it
        protocol.Batches.ShouldBe(1);
        await theRuntime.Storage.Outbox.Received(1)
            .DeleteOutgoingAsync(Arg.Is<Envelope[]>(x => x.Length == 1 && x[0] == accepted));
    }

    [Fact]
    public async Task the_inline_send_goes_through_the_protocol_of_a_tenants_batched_sender()
    {
        await using var broker = new FakeTcpBroker();

        var tenantEndpoint = new TcpEndpoint(broker.Port);
        var tenantSender = new BatchedSender(tenantEndpoint, new SocketSenderProtocol(), CancellationToken.None,
            NullLogger.Instance);

        var endpoint = new TcpEndpoint(deadPort());
        var tenanted = new TenantedSender(endpoint.Uri, TenantedIdBehavior.TenantIdRequired, null);
        tenanted.RegisterSender("tenant-a", tenantSender);
        var agent = durableAgent(endpoint, tenanted);

        var envelope = envelopeTo(agent);
        envelope.TenantId = "tenant-a";
        var context = await contextWithOutgoing(envelope);

        await context.SendAllOutgoingMessagesInlineAsync();

        // TenantedSender.SendAsync would only have queued it inside the tenant's BatchedSender
        broker.Received.ShouldBe(1);
        await theRuntime.Storage.Outbox.Received(1)
            .DeleteOutgoingAsync(Arg.Is<Envelope[]>(x => x.Length == 1 && x[0] == envelope));
    }

    [Fact]
    public async Task the_durability_pass_recovers_one_destination_in_one_round_trip()
    {
        await using var broker = new FakeTcpBroker();
        var agent = durableTcpAgent(broker.Port);
        var envelopes = new[] { envelopeTo(agent), envelopeTo(agent), envelopeTo(agent) };

        var report = await recoverAsync(envelopes);

        report.OutgoingSent.ShouldBe(3);
        broker.Received.ShouldBe(1);
        await theRuntime.Storage.Outbox.Received(1)
            .DeleteOutgoingAsync(Arg.Is<Envelope[]>(x => x.Length == 3));
    }

    [Fact]
    public async Task the_durability_pass_gives_up_on_a_hung_send_at_its_deadline()
    {
        // A broker send takes no cancellation token, so a hung one used to hold the pass, and its lock, forever
        var endpoint = new TcpEndpoint(deadPort());
        var agent = durableAgent(endpoint,
            new BatchedSender(endpoint, new HangingProtocol(), CancellationToken.None, NullLogger.Instance));
        var envelope = envelopeTo(agent);
        envelope.Sender = null;

        var source = Substitute.For<IStaleOutgoingSource>();
        source.LoadStaleOutgoingAsync(default, default!, default, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(new List<Envelope> { envelope });

        var step = new RecoverStaleOutgoingStep(theRuntime.Storage, source, NullLogger.Instance)
        {
            SendGrace = 100.Milliseconds()
        };
        var context = new DurabilityPassContext(theRuntime, theRuntime.Storage, 10,
            DateTimeOffset.UtcNow.AddMilliseconds(200), () => DateTimeOffset.UtcNow);

        var result = await step.ExecuteAsync(context, TestContext.Current.CancellationToken)
            .WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        result.OutgoingFailed.ShouldBe(1);
        result.OutgoingSent.ShouldBe(0);
        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
    }

    private Task<DurabilityPassStepResult> recoverAsync(params Envelope[] envelopes)
    {
        // A recovered row has no sending agent of its own; the step finds it by destination
        foreach (var envelope in envelopes) envelope.Sender = null;

        var source = Substitute.For<IStaleOutgoingSource>();
        source.LoadStaleOutgoingAsync(default, default!, default, default)
            .ReturnsForAnyArgs(envelopes.ToList());

        var step = new RecoverStaleOutgoingStep(theRuntime.Storage, source, NullLogger.Instance);
        var context = new DurabilityPassContext(theRuntime, theRuntime.Storage, 10,
            DateTimeOffset.UtcNow.AddMinutes(1), () => DateTimeOffset.UtcNow);

        return step.ExecuteAsync(context, CancellationToken.None);
    }

    private class HangingProtocol : ISenderProtocol
    {
        public Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
            => new TaskCompletionSource().Task;
    }

    /// <summary>
    /// Answers for part of a batch the way SQS does: the accepted envelopes and the rejected ones come back as
    /// separate sub-batches. An "ignored" envelope is in neither, as Azure Service Bus does with one it cannot map
    /// </summary>
    private class PartlyAcceptingProtocol : ISenderProtocol
    {
        public HashSet<Guid> Rejected { get; } = new();
        public HashSet<Guid> Ignored { get; } = new();
        public int Batches { get; private set; }

        public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
        {
            Batches++;

            var accepted = batch.Messages.Where(x => !Rejected.Contains(x.Id) && !Ignored.Contains(x.Id)).ToList();
            var rejected = batch.Messages.Where(x => Rejected.Contains(x.Id)).ToList();

            await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, accepted));
            await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, rejected),
                new InvalidOperationException("Throttled"));
        }
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
