using JasperFx.Core;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Wolverine.Transports.Sending;

namespace Wolverine.Nats.Internal;

public class NatsSender : ISender
{
    private readonly NatsEndpoint _endpoint;
    private readonly ILogger<NatsEndpoint> _logger;
    private readonly NatsEnvelopeMapper _mapper;
    private readonly CancellationToken _cancellation;
    private readonly INatsPublisher _publisher;
    private readonly bool _supportsNativeScheduledSend;

    internal NatsSender(
        NatsEndpoint endpoint,
        INatsPublisher publisher,
        ILogger<NatsEndpoint> logger,
        NatsEnvelopeMapper mapper,
        CancellationToken cancellation,
        bool supportsNativeScheduledSend
    )
    {
        _endpoint = endpoint;
        _publisher = publisher;
        _logger = logger;
        _mapper = mapper;
        _cancellation = cancellation;
        _supportsNativeScheduledSend = supportsNativeScheduledSend;
        Destination = endpoint.Uri;
    }

    internal static NatsSender Create(
        NatsEndpoint endpoint,
        NatsConnection connection,
        INatsJSContext? jetStreamContext,
        ILogger<NatsEndpoint> logger,
        NatsEnvelopeMapper mapper,
        CancellationToken cancellation,
        bool useJetStream,
        bool supportsNativeScheduledSend
    )
    {
        INatsPublisher publisher = useJetStream
            ? new JetStreamPublisher(connection, jetStreamContext!, logger, endpoint.ScheduleSubjectSuffix, endpoint.MsgIdSource)
            : new CoreNatsPublisher(connection, logger);

        return new NatsSender(endpoint, publisher, logger, mapper, cancellation, supportsNativeScheduledSend);
    }

    public bool SupportsNativeScheduledSend => _supportsNativeScheduledSend;
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

            string targetSubject;
            string? replyTo = null;

            if (envelope.IsResponse && envelope.Destination != null)
            {
                // For response messages, Wolverine sets Destination to the original sender's reply URI
                // We need to use Destination, not ReplyUri (which would be our own reply endpoint)
                targetSubject = NatsTransport.ExtractSubjectFromUri(envelope.Destination);

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
                // Per-message subject routing: when the endpoint is RoutingMode.ByTopic (see
                // PublishMessagesToNatsSubject<T> / IMessageBus.BroadcastToTopicAsync) Wolverine
                // stamps the computed subject onto Envelope.TopicName. Static endpoints leave it
                // null and fall back to the endpoint's fixed subject. Mirrors RabbitMqSender and
                // InlineKafkaSender.
                targetSubject = envelope.TopicName.IsNotEmpty()
                    ? _endpoint.NormalizeSubject(envelope.TopicName)
                    : _endpoint.Subject;

                // Advanced escape hatch: rewrite the subject from envelope-level state (headers,
                // tenant, aggregate id) that the strongly-typed subject function can't express.
                if (_endpoint.SubjectResolver is { } resolver)
                {
                    targetSubject = resolver.ResolveSubject(targetSubject, envelope);
                }

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
