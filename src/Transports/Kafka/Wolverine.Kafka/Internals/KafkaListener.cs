using System.Text;
using Confluent.Kafka;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Kafka.Internals;

public class KafkaListener : IListener, IDisposable, ISupportDeadLetterQueue
{
    private readonly KafkaTopic _endpoint;
    private readonly IConsumer<string, byte[]> _consumer;
    private CancellationTokenSource _cancellation = new();
    private readonly Task _runner;
    private readonly IReceiver _receiver;
    private readonly string? _messageTypeName;
    private readonly QualityOfService _qualityOfService;
    private readonly QualityOfService? _requestedQualityOfService;
    private readonly ILogger _logger;

    public KafkaListener(KafkaTopic topic, ConsumerConfig config,
        IConsumer<string, byte[]> consumer, IReceiver receiver,
        ILogger<KafkaListener> logger)
    {
        _endpoint = topic;
        _logger = logger;
        Address = topic.Uri;
        _consumer = consumer;
        _logger = logger;
        var mapper = topic.EnvelopeMapper;

        _messageTypeName = topic.MessageType?.ToMessageTypeName();

        Config = config;
        _receiver = receiver;

        _requestedQualityOfService = topic.QualityOfService;

        _qualityOfService = _requestedQualityOfService
            ?? (Config.EnableAutoCommit.HasValue && !Config.EnableAutoCommit.Value
            ? QualityOfService.AtMostOnce
                : QualityOfService.AtLeastOnce);

        _runner = Task.Run(async () =>
        {
            _consumer.Subscribe(topic.TopicName);
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    if (_qualityOfService == QualityOfService.AtMostOnce)
                    {
                        try
                        {
                            _consumer.Commit();
                        }
                        catch (KafkaException e)
                        {
                            if (!e.Message.Contains("No offset stored"))
                            {
                                throw;
                            }
                        }
                    }

                    try
                    {
                        var result = _consumer.Consume(_cancellation.Token);
                        var message = result.Message;

                        var envelope = mapper.CreateEnvelope(result.Topic, message);
                        envelope.Offset = result.Offset.Value;
                        envelope.Partition = result.Partition.Value;
                        envelope.MessageType ??= _messageTypeName;
                        envelope.GroupId = Config.GroupId;

                        await _receiver.ReceivedAsync(this, envelope);
                    }
                    catch (OperationCanceledException)
                    {
                        // we're done here!
                    }
                    catch (Exception e)
                    {
                        // Might be a poison pill message, try to get out of here
                        try
                        {
                            _consumer.Commit();
                        }
                        // ReSharper disable once EmptyGeneralCatchClause
                        catch (Exception)
                        {
                        }

                        _logger.LogError(e, "Error trying to map Kafka message to a Wolverine envelope");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down
            }
            finally
            {
                _consumer.Close();
            }
        }, _cancellation.Token);
    }

    public ConsumerConfig Config { get; }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (_requestedQualityOfService == QualityOfService.AtLeastOnce)
        {
            var tpo = new TopicPartitionOffset(
                envelope.TopicName,
                new Partition(envelope.Partition),
                new Offset(envelope.Offset + 1));

            _consumer.StoreOffset(tpo);

            // If auto-commit is disabled, we need to manually commit after storing
            if (Config.EnableAutoCommit.HasValue && !Config.EnableAutoCommit.Value)
            {
                TryCommit();
            }

            return ValueTask.CompletedTask;
        }

        if (_qualityOfService == QualityOfService.AtLeastOnce)
        {
            TryCommit();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        // Really just a retry
        return _receiver.ReceivedAsync(this, envelope);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public Uri Address { get; }
    public async ValueTask StopAsync()
    {
        _cancellation.Cancel();
        await _runner;
    }

    public bool NativeDeadLetterQueueEnabled => _endpoint.NativeDeadLetterQueueEnabled;

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        var transport = _endpoint.Parent;
        var dlqTopicName = transport.DeadLetterQueueTopicName;

        try
        {
            var message = await _endpoint.EnvelopeMapper!.CreateMessage(envelope);

            message.Headers ??= new Headers();
            message.Headers.Add(DeadLetterQueueConstants.ExceptionTypeHeader, Encoding.UTF8.GetBytes(exception.GetType().FullName ?? "Unknown"));
            message.Headers.Add(DeadLetterQueueConstants.ExceptionMessageHeader, Encoding.UTF8.GetBytes(exception.Message));
            message.Headers.Add(DeadLetterQueueConstants.ExceptionStackHeader, Encoding.UTF8.GetBytes(exception.StackTrace ?? ""));
            message.Headers.Add(DeadLetterQueueConstants.FailedAtHeader, Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));

            using var producer = transport.CreateProducer(_endpoint.GetEffectiveProducerConfig());
            await producer.ProduceAsync(dlqTopicName, message);
            producer.Flush();

            _logger.LogInformation(
                "Moved envelope {EnvelopeId} to dead letter queue topic {DlqTopic}. Exception: {ExceptionType}: {ExceptionMessage}",
                envelope.Id, dlqTopicName, exception.GetType().Name, exception.Message);

            try
            {
                _consumer.Commit();
            }
            catch (Exception commitEx)
            {
                _logger.LogWarning(commitEx,
                    "Error committing offset after moving envelope {EnvelopeId} to dead letter queue",
                    envelope.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to move envelope {EnvelopeId} to dead letter queue topic {DlqTopic}",
                envelope.Id, dlqTopicName);
            throw;
        }
    }

    public void Dispose()
    {
        _consumer.SafeDispose();
        _runner.Dispose();
    }

    private void TryCommit()
    {
        try
        {
            _consumer.Commit();
        }
        catch (KafkaException)
        {
        }
    }
}