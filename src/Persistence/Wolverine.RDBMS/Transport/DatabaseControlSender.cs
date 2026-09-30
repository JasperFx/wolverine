using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace Wolverine.RDBMS.Transport;

internal class DatabaseControlSender : ISender, IAsyncDisposable
{
    private readonly DatabaseControlEndpoint _endpoint;
    private readonly ILogger _logger;
    private readonly RetryBlock<Envelope> _retryBlock;
    private readonly DatabaseControlTransport _transport;

    public DatabaseControlSender(DatabaseControlEndpoint endpoint, DatabaseControlTransport transport, ILogger logger,
        CancellationToken cancellationToken)
    {
        _endpoint = endpoint;
        _transport = transport;
        _logger = logger;
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
            await using var conn = await _transport.Database.DataSource.OpenConnectionAsync();
            await conn.CloseAsync();
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
        if (cancellationToken.IsCancellationRequested || _transport.Database.HasDisposed)
        {
            return;
        }

        var body = EnvelopeSerializer.Serialize(envelope);

        // GH-4718: EnvelopeSerializer enforces MaxDataSize when READING and never when writing, so Wolverine
        // was happily writing its own control traffic in a size it then refuses to read back. The destination
        // is a different process and may be configured differently -- during a rolling deploy it may not even
        // be the same Wolverine version -- so this warns rather than refuses, but it puts the sender, the
        // destination and the message type in the log of the node that CAUSED the problem. Without it the only
        // trace is an InvalidEnvelopeException on the receiving node naming nothing but "IAgentCommand".
        var maxDataSize = EnvelopeSerializer.Limits.MaxDataSize;
        if (envelope.Data is { Length: > 0 } data && data.Length > maxDataSize)
        {
            _logger.LogWarning(
                "Control message {MessageType} being sent to node {NodeId} carries {DataLength} bytes of data, which exceeds this host's Options.MaxIncomingEnvelopeDataSize of {MaxDataSize}. Unless the destination is configured with a higher limit it will reject this message outright.",
                envelope.MessageType, _endpoint.NodeId, data.Length, maxDataSize);
        }

        try
        {
            await _transport.Database.DataSource.CreateCommand(
                    $"insert into {_transport.TableName} (id, message_type, node_id, body, expires) values (@id, @messagetype, @node, @body, @expires)")
                .With("id", envelope.Id)
                .With("messagetype", envelope.MessageType!)
                .With("node", _endpoint.NodeId)
                .With("body", body)
                .With("expires", DateTimeOffset.UtcNow.AddSeconds(30)).ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }
}