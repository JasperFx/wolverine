using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals.Transport;

internal class RavenDbControlListener : IListener
{
    private readonly CancellationTokenSource _cancellation;
    private readonly IReceiver _receiver;
    private readonly Task _receivingLoop;
    private readonly RetryBlock<Envelope> _retryBlock;
    private readonly RavenDbControlTransport _transport;

    public RavenDbControlListener(RavenDbControlTransport transport, RavenDbControlEndpoint endpoint,
        IReceiver receiver, ILogger<RavenDbControlListener> logger, CancellationToken cancellationToken)
    {
        _transport = transport;
        _receiver = receiver;

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Address = endpoint.Uri;

        _retryBlock = new RetryBlock<Envelope>(deleteEnvelopeAsync, logger, cancellationToken);

        _receivingLoop = Task.Run(async () =>
        {
            await Task.Delay(Random.Shared.Next(100, 1000).Milliseconds(), _cancellation.Token);

            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await pollForMessagesAsync();
                }
                catch (OperationCanceledException)
                {
                    // Shutting down
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error trying to poll for messages from the RavenDb control queue");
                }

                await Task.Delay(1.Seconds(), _cancellation.Token);
            }
        }, _cancellation.Token);
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        await _retryBlock.PostAsync(envelope);
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _receivingLoop.SafeDispose();
        _retryBlock.SafeDispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
    }

    private async Task pollForMessagesAsync()
    {
        var nodeId = _transport.Options.UniqueNodeId;

        using var session = _transport.Store.OpenAsyncSession();

        var messages = await session
            .Query<ControlMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.NodeId == nodeId)
            .ToListAsync(_cancellation.Token);

        if (messages.Count == 0)
        {
            return;
        }

        var envelopes = new List<Envelope>();
        foreach (var message in messages)
        {
            var envelope = EnvelopeSerializer.Deserialize(message.Body);
            envelopes.Add(envelope);
        }

        await _receiver.ReceivedAsync(this, envelopes.ToArray());
        await _transport.DeleteEnvelopesAsync(envelopes, _cancellation.Token);
    }

    private async Task deleteEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        using var session = _transport.Store.OpenAsyncSession();
        session.Delete(ControlMessage.IdFor(envelope.Id));
        await session.SaveChangesAsync(cancellationToken);
    }
}
