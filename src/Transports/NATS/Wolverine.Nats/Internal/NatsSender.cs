using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using Wolverine.Transports.Sending;

namespace Wolverine.Nats.Internal;

public class NatsSender : ISender
{
    private readonly NatsEndpoint _endpoint;
    private readonly ILogger<NatsEndpoint> _logger;
    private readonly NatsEnvelopeMapper _mapper;
    private readonly CancellationToken _cancellation;
    private readonly INatsPublisher _publisher;

    internal NatsSender(
        NatsEndpoint endpoint,
        INatsPublisher publisher,
        ILogger<NatsEndpoint> logger,
        NatsEnvelopeMapper mapper,
        CancellationToken cancellation
    )
    {
        _endpoint = endpoint;
        _publisher = publisher;
        _logger = logger;
        _mapper = mapper;
        _cancellation = cancellation;
        Destination = endpoint.Uri;
    }

    internal static NatsSender Create(
        NatsEndpoint endpoint,
        NatsConnection connection,
        ILogger<NatsEndpoint> logger,
        NatsEnvelopeMapper mapper,
        CancellationToken cancellation,
        bool useJetStream
    )
    {
        INatsPublisher publisher = useJetStream
            ? new JetStreamPublisher(connection, logger)
            : new CoreNatsPublisher(connection, logger);

        return new NatsSender(endpoint, publisher, logger, mapper, cancellation);
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        return await _publisher.PingAsync(_cancellation);
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        try
        {
            var headers = new NatsHeaders();
            _mapper.MapEnvelopeToOutgoing(envelope, headers);

            foreach (var header in _endpoint.CustomHeaders)
            {
                headers[header.Key] = header.Value;
            }

            var data = envelope.Data ?? Array.Empty<byte>();

            var targetSubject = _endpoint.Subject;
            string? replyTo = null;

            if (envelope.IsResponse && envelope.ReplyUri != null)
            {
                targetSubject = NatsTransport.ExtractSubjectFromUri(envelope.ReplyUri);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Sending reply message {MessageId} to reply subject {ReplySubject}",
                        envelope.Id,
                        targetSubject
                    );
                }
            }
            else
            {
                if (envelope.ReplyRequested != null && envelope.ReplyUri != null)
                {
                    replyTo = NatsTransport.ExtractSubjectFromUri(envelope.ReplyUri);

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "Sending request message {MessageId} to NATS subject {Subject} with reply-to {ReplyTo} (expecting {ReplyType})",
                            envelope.Id,
                            targetSubject,
                            replyTo,
                            envelope.ReplyRequested
                        );
                    }
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "Sending message {MessageId} to NATS subject {Subject}",
                            envelope.Id,
                            targetSubject
                        );
                    }
                }
            }

            await _publisher.PublishAsync(
                targetSubject,
                data,
                headers,
                replyTo,
                envelope,
                _cancellation
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to send message {MessageId} to NATS subject {Subject}",
                envelope.Id,
                _endpoint.Subject
            );
            throw;
        }
    }
}
