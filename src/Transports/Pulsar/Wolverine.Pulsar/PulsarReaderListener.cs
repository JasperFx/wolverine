using System.Buffers;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

/// <summary>
/// Ephemeral "hot-tail" / broadcast listener (GH-3184) built on a non-durable Pulsar <c>Reader</c>
/// starting at <see cref="MessageId.Latest"/>. Each process gets its own throwaway, auto-named reader
/// cursor, so every node receives all messages published after it joins and never replays history — the
/// idiomatic Pulsar pattern for live dashboards and fan-out-to-all. No subscription cursor is committed,
/// so there is nothing to acknowledge: <see cref="CompleteAsync"/> / <see cref="DeferAsync"/> are no-ops.
/// </summary>
internal class PulsarReaderListener : IListener, IReportConnectionState
{
    private readonly CancellationTokenSource _localCancellation;
    private readonly IReader<ReadOnlySequence<byte>> _reader;
    private readonly Task _receivingLoop;

    // GH-3231: updated by the reader's StateChangedHandler (registered on the builder below). Volatile because it is
    // written from DotPulsar's state-change callback and read by external health probes.
    private volatile TransportConnectionState _connectionState = TransportConnectionState.Disconnected;

    public TransportConnectionState ConnectionState => _connectionState;

    public PulsarReaderListener(IWolverineRuntime runtime, PulsarEndpoint endpoint, IReceiver receiver,
        PulsarTransport transport, CancellationToken cancellation)
    {
        if (receiver == null)
        {
            throw new ArgumentNullException(nameof(receiver));
        }

        Address = endpoint.Uri;

        var mapper = endpoint.BuildMapper(runtime);

        _localCancellation = new CancellationTokenSource();
        var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _localCancellation.Token);

        var readerBuilder = transport.Client!.NewReader()
            .Topic(endpoint.PulsarTopic())
            .StartMessageId(MessageId.Latest)
            // GH-3231: track the reader's connection state so health probes can read it (see ConnectionState).
            .StateChangedHandler(changed => _connectionState = changed.ReaderState.ToTransportConnectionState());

        _reader = readerBuilder.Create();

        _receivingLoop = Task.Run(async () =>
        {
            await foreach (var message in _reader.Messages(combined.Token))
            {
                var envelope = new PulsarEnvelope(message)
                {
                    Data = message.Data.ToArray()
                };

                mapper.MapIncomingToEnvelope(envelope, message);

                await receiver.ReceivedAsync(this, envelope);
            }
        }, combined.Token);

        Pipeline = receiver.Pipeline;
    }

    public Uri Address { get; }

    public IHandlerPipeline? Pipeline { get; }

    // A Reader cursor is non-durable and unacknowledged: there is nothing to complete or defer.
    public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;

    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

    public ValueTask StopAsync()
    {
        // Nothing to unsubscribe — the reader cursor is throwaway. Tear-down happens in DisposeAsync.
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _localCancellation.CancelAsync();
        _localCancellation.Dispose();

        await _reader.DisposeAsync();

        _receivingLoop.Dispose();
    }
}
