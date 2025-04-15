using System.Buffers;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

internal class PulsarListener : IListener
{
    private readonly CancellationToken _cancellation;
    private readonly IConsumer<ReadOnlySequence<byte>>? _consumer;
    private readonly CancellationTokenSource _localCancellation;
    private readonly Task? _receivingLoop;
    private readonly PulsarSender? _sender;
    private readonly bool _enableRequeue;

    public PulsarListener(IWolverineRuntime runtime, PulsarEndpoint endpoint, IReceiver receiver,
        PulsarTransport transport,
        CancellationToken cancellation)
    {
        if (receiver == null)
        {
            throw new ArgumentNullException(nameof(receiver));
        }

        _cancellation = cancellation;

        Address = endpoint.Uri;

        _enableRequeue = endpoint.EnableRequeue;

        if (_enableRequeue)
        {
            _sender = new PulsarSender(runtime, endpoint, transport, _cancellation);
        }

        var mapper = endpoint.BuildMapper(runtime);

        _localCancellation = new CancellationTokenSource();

        var combined = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, _localCancellation.Token);

        _consumer = transport.Client!.NewConsumer()
            .SubscriptionName(endpoint.SubscriptionName)
            .SubscriptionType(endpoint.SubscriptionType)
            .Topic(endpoint.PulsarTopic())
            .Create();

        _receivingLoop = Task.Run(async () =>
        {
            await foreach (var message in _consumer.Messages(combined.Token))
            {
                var envelope = new PulsarEnvelope(message)
                {
                    Data = message.Data.ToArray()
                };

                mapper.MapIncomingToEnvelope(envelope, message);

                await receiver.ReceivedAsync(this, envelope);
            }
        }, combined.Token);
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is PulsarEnvelope e)
        {
            if (_consumer != null)
            {
                return _consumer.Acknowledge(e.MessageData, _cancellation);
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (!_enableRequeue)
        {
            throw new InvalidOperationException("Requeue is not enabled for this endpoint");
        }

        if (_sender is not null && envelope is PulsarEnvelope e)
        {
            await _consumer!.Acknowledge(e.MessageData, _cancellation);
            await _sender.SendAsync(envelope);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _localCancellation.Cancel();

        if (_consumer != null)
        {
            await _consumer.DisposeAsync();
        }

        if (_sender != null)
        {
            await _sender.DisposeAsync();
        }

        _receivingLoop!.Dispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        if (_consumer == null)
        {
            return;
        }

        await _consumer.Unsubscribe(_cancellation);
        await _consumer.RedeliverUnacknowledgedMessages(_cancellation);
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (!_enableRequeue)
        {
            throw new InvalidOperationException("Requeue is not enabled for this endpoint");
        }

        if (_sender is not null && envelope is PulsarEnvelope)
        {
            await _sender.SendAsync(envelope);
            return true;
        }

        return false;
    }
}
