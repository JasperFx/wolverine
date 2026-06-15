using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus.Internal;

/// <summary>
/// Configuration holder for Azure Service Bus dead letter queue recovery. Registered as a singleton
/// so the listener can discover which dead letter sources to drain. When no names are supplied, the
/// listener recovers from both the Wolverine-managed dead letter queue(s) and the native
/// <c>$DeadLetterQueue</c> sub-queue of every listening queue and subscription.
/// </summary>
public class AzureServiceBusDeadLetterQueueRecoverySettings
{
    /// <summary>
    /// Optional set of names to restrict recovery to. A name may be a Wolverine-managed dead letter
    /// queue name, a listening queue name, or a subscription endpoint name. When empty, every
    /// managed dead letter queue and every listening queue/subscription's native dead letter
    /// sub-queue is drained.
    /// </summary>
    public List<string> EndpointNames { get; } = new();
}

/// <summary>
/// Background service that recovers Azure Service Bus dead letters into Wolverine's durable dead
/// letter storage (the <c>wolverine_dead_letters</c> table), so natively dead-lettered messages
/// become queryable and replayable through <see cref="Wolverine.Persistence.Durability.IDeadLetters"/>
/// and tools like CritterWatch. It drains two kinds of source:
/// <list type="bullet">
///   <item>The Wolverine-managed dead letter queue(s) (default <c>wolverine-dead-letter-queue</c>),
///   where buffered and durable endpoints move failed messages with the exception metadata stamped
///   onto the message.</item>
///   <item>The native <c>$DeadLetterQueue</c> sub-queue of each listening queue and subscription,
///   where inline endpoints and Azure Service Bus itself (TTL / max-delivery) dead-letter messages,
///   reading the native <c>DeadLetterReason</c>/<c>DeadLetterErrorDescription</c>.</item>
/// </list>
/// This mirrors the RabbitMQ <c>EnableDeadLetterQueueRecovery()</c> feature.
/// </summary>
public class AzureServiceBusDeadLetterQueueListener : BackgroundService
{
    private readonly AzureServiceBusTransport _transport;
    private readonly IWolverineRuntime _runtime;
    private readonly AzureServiceBusDeadLetterQueueRecoverySettings _settings;
    private readonly ILogger<AzureServiceBusDeadLetterQueueListener> _logger;

    public AzureServiceBusDeadLetterQueueListener(AzureServiceBusTransport transport, IWolverineRuntime runtime,
        AzureServiceBusDeadLetterQueueRecoverySettings settings,
        ILogger<AzureServiceBusDeadLetterQueueListener> logger)
    {
        _transport = transport;
        _runtime = runtime;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Describes one dead letter source to drain. A managed source is a normal Wolverine dead letter
    /// queue (full envelope, exception metadata in application properties); a sub-queue source is a
    /// native <c>$DeadLetterQueue</c> (exception metadata in DeadLetterReason/DeadLetterErrorDescription).
    /// </summary>
    private sealed record DrainSource(string Name, bool IsSubQueue, AzureServiceBusEndpoint? Endpoint,
        Func<ServiceBusReceiver> CreateReceiver);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var sources = resolveSources();
            if (sources.Count == 0)
            {
                _logger.LogInformation(
                    "Azure Service Bus dead letter queue recovery was enabled, but no dead letter sources were found. No recovery listeners were started.");
                return;
            }

            var tasks = sources.Select(source => drainLoopAsync(source, stoppingToken)).ToArray();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Azure Service Bus dead letter queue recovery listener failed");
        }
    }

    private List<DrainSource> resolveSources()
    {
        var listeningQueues = _transport.Queues.Where(x => x.IsListener).ToArray();
        var listeningSubscriptions = _transport.Subscriptions.Where(x => x.IsListener).ToArray();

        var sources = new List<DrainSource>();

        // Wolverine-managed dead letter queues — where buffered/durable endpoints move failures.
        var managedDlqNames = listeningQueues
            .Select(x => x.DeadLetterQueueName)
            .Where(x => x.IsNotEmpty())
            .Distinct()
            .Select(x => x!);

        foreach (var name in managedDlqNames)
        {
            var dlqName = name;
            // Resolve the managed dead letter queue as a real endpoint so the recovered envelope gets
            // a non-null Destination/Uri and that queue's own envelope mapper.
            var dlqEndpoint = _transport.Queues[dlqName];
            sources.Add(new DrainSource(dlqName, false, dlqEndpoint,
                () => _transport.BusClient.CreateReceiver(dlqName)));
        }

        // Native $DeadLetterQueue sub-queues — inline endpoints and ASB-native dead letters.
        foreach (var queue in listeningQueues)
        {
            var queueName = queue.QueueName;
            sources.Add(new DrainSource(queueName, true, queue,
                () => _transport.BusClient.CreateReceiver(queueName,
                    new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter })));
        }

        foreach (var subscription in listeningSubscriptions)
        {
            var sub = subscription;
            sources.Add(new DrainSource(sub.SubscriptionName, true, sub,
                () => _transport.BusClient.CreateReceiver(sub.Topic.TopicName, sub.SubscriptionName,
                    new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter })));
        }

        if (_settings.EndpointNames.Count > 0)
        {
            var allowed = _settings.EndpointNames.Select(x => _transport.SanitizeIdentifier(x)).ToHashSet();
            sources = sources.Where(x => allowed.Contains(_transport.SanitizeIdentifier(x.Name))).ToList();
        }

        return sources;
    }

    private async Task drainLoopAsync(DrainSource source, CancellationToken stoppingToken)
    {
        var failedCount = 0;
        ServiceBusReceiver? receiver = null;

        // The managed dead letter queue holds full Wolverine envelopes; use a real endpoint's mapper
        // when we have one, otherwise fall back to the default mapper.
        var mapper = (source.Endpoint ?? _transport.Queues.FirstOrDefault(x => x.IsListener))?.BuildMapper(_runtime);

        _logger.LogInformation(
            "Started Azure Service Bus dead letter recovery listener on '{Source}' ({Kind}). Messages will be recovered to durable storage.",
            source.Name, source.IsSubQueue ? "native sub-queue" : "managed queue");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    receiver ??= source.CreateReceiver();

                    var messages = await receiver.ReceiveMessagesAsync(20, 5.Seconds(), stoppingToken);

                    failedCount = 0;

                    if (messages == null || messages.Count == 0)
                    {
                        continue;
                    }

                    foreach (var message in messages)
                    {
                        await recoverMessageAsync(source, mapper, receiver, message, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    failedCount++;
                    var pause = failedCount > 5 ? 5.Seconds() : (failedCount * 200).Milliseconds();
                    _logger.LogError(e,
                        "Error while recovering dead letters from Azure Service Bus source '{Source}'", source.Name);

                    // Rebuild the receiver on the next pass in case the connection went bad
                    if (receiver != null)
                    {
                        await receiver.DisposeAsync();
                        receiver = null;
                    }

                    await Task.Delay(pause, stoppingToken);
                }
            }
        }
        finally
        {
            if (receiver != null)
            {
                await receiver.DisposeAsync();
            }
        }
    }

    private async Task recoverMessageAsync(DrainSource source, IAzureServiceBusEnvelopeMapper? mapper,
        ServiceBusReceiver receiver, ServiceBusReceivedMessage message, CancellationToken token)
    {
        try
        {
            var envelope = new Envelope();

            try
            {
                if (mapper != null)
                {
                    mapper.MapIncomingToEnvelope(envelope, message);
                }
                else
                {
                    envelope.Data = message.Body.ToArray();
                    envelope.ContentType = message.ContentType ?? EnvelopeConstants.JsonContentType;
                    envelope.MessageType = message.Subject;
                }
            }
            catch (Exception readError)
            {
                _logger.LogWarning(readError,
                    "Could not reconstruct a Wolverine envelope from Azure Service Bus dead letter message {MessageId} on '{Source}'. Persisting a minimal envelope.",
                    message.MessageId, source.Name);

                envelope = new Envelope
                {
                    Data = message.Body.ToArray(),
                    ContentType = message.ContentType ?? EnvelopeConstants.JsonContentType,
                    MessageType = message.Subject ?? "unknown"
                };
            }

            if (envelope.Id == Guid.Empty)
            {
                envelope.Id = Guid.NewGuid();
            }

            envelope.Destination ??= source.Endpoint?.Uri;
            envelope.Source ??= source.Endpoint?.Uri.ToString() ?? source.Name;
            if (envelope.SentAt == default)
            {
                envelope.SentAt = DateTimeOffset.UtcNow;
            }

            var (exceptionType, exceptionMessage) = resolveExceptionInfo(envelope, message);
            var exception = new DeadLetterRecoveredException(exceptionType, exceptionMessage);
            await _runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);

            // Only settle the dead letter once it is safely persisted.
            await receiver.CompleteMessageAsync(message, token);

            _logger.LogInformation(
                "Recovered dead letter {MessageId} (type={MessageType}) from Azure Service Bus '{Source}' to durable storage. Reason: {Reason}",
                envelope.Id, envelope.MessageType ?? "unknown", source.Name, exceptionType);
        }
        catch (Exception e)
        {
            // Leave the message in place (its lock expires and it returns) so a transient storage
            // failure doesn't lose the dead letter.
            _logger.LogError(e,
                "Failed to recover Azure Service Bus dead letter message {MessageId} from '{Source}'",
                message.MessageId, source.Name);
        }
    }

    private static (string exceptionType, string exceptionMessage) resolveExceptionInfo(Envelope envelope,
        ServiceBusReceivedMessage message)
    {
        // Wolverine stamps these onto the message (and thus the envelope headers) when it moves a
        // failed message to its managed dead letter queue.
        var type = headerOrNull(envelope, DeadLetterQueueConstants.ExceptionTypeHeader)
                   ?? (message.DeadLetterReason.IsNotEmpty() ? message.DeadLetterReason : null)
                   ?? "Unknown";

        var description = headerOrNull(envelope, DeadLetterQueueConstants.ExceptionMessageHeader)
                          ?? (message.DeadLetterErrorDescription.IsNotEmpty() ? message.DeadLetterErrorDescription : null)
                          ?? "Recovered from Azure Service Bus dead letter queue";

        return (type!, description!);
    }

    private static string? headerOrNull(Envelope envelope, string key)
    {
        return envelope.Headers.TryGetValue(key, out var value) && value.IsNotEmpty() ? value : null;
    }
}
