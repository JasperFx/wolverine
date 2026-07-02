using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RavenDb.Internals.Transport;

internal class RavenDbControlSender : ISender, IAsyncDisposable
{
    private readonly RavenDbControlEndpoint _endpoint;
    private readonly RetryBlock<Envelope> _retryBlock;
    private readonly RavenDbControlTransport _transport;

    public RavenDbControlSender(RavenDbControlEndpoint endpoint, RavenDbControlTransport transport,
        ILogger logger, CancellationToken cancellationToken)
    {
        _endpoint = endpoint;
        _transport = transport;
        Destination = endpoint.Uri;

        _retryBlock = new RetryBlock<Envelope>(sendMessageAsync, logger, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _retryBlock.DrainAsync();
        _retryBlock.Dispose();
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        try
        {
            using var session = _transport.Store.OpenAsyncSession();
            await session.Advanced.ExistsAsync(ControlMessage.IdFor(Guid.Empty));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        envelope.DeliverWithin = 10.Seconds();

        await _retryBlock.PostAsync(envelope);
    }

    private async Task sendMessageAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var expires = DateTimeOffset.UtcNow.AddSeconds(30);
        var message = new ControlMessage(envelope, _endpoint.NodeId, EnvelopeSerializer.Serialize(envelope), expires);

        using var session = _transport.Store.OpenAsyncSession();
        await session.StoreAsync(message, message.Id, cancellationToken);

        // Best-effort TTL cleanup safety net for control messages that are never
        // consumed (e.g. the target node dies before polling). Requires the RavenDB
        // Expiration feature; when disabled the listener's own delete-after-receipt
        // path plus the poll-loop sweep still keep the collection bounded.
        session.Advanced.GetMetadataFor(message)[Raven.Client.Constants.Documents.Metadata.Expires]
            = expires.UtcDateTime;

        await session.SaveChangesAsync(cancellationToken);
    }
}
