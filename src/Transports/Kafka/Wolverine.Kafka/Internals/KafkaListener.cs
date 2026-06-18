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
    private readonly ILogger _logger;
    private readonly KafkaOffsetCommitter _committer;
    public KafkaListener(KafkaTopic topic, ConsumerConfig config,
        IConsumer<string, byte[]> consumer, IReceiver receiver,
        ILogger<KafkaListener> logger)
    {
        _endpoint = topic;
        _logger = logger;
        Address = topic.Uri;
        _consumer = consumer;
        var mapper = topic.EnvelopeMapper;

        _messageTypeName = topic.MessageType?.ToMessageTypeName();

        Config = config;
        _receiver = receiver;
        _committer = new KafkaOffsetCommitter(consumer, config, topic.CommitMode, topic.CommitBatchCount,
            topic.CommitBatchInterval, logger);

        _runner = Task.Run(async () =>
        {
            _consumer.Subscribe(topic.TopicName);
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    ConsumeResult<string, byte[]>? result = null;
                    try
                    {
                        result = _consumer.Consume(_cancellation.Token);
                        var message = result.Message;

                        var envelope = mapper!.CreateEnvelope(result.Topic, message);
                        envelope.TopicName = result.Topic;
                        envelope.Offset = result.Offset.Value;
                        envelope.PartitionId = result.Partition.Value;
                        envelope.MessageType ??= _messageTypeName;

                        if (topic.GroupByMessageKey)
                        {
                            // GH-3140: shard by-key processing on the Kafka message key.
                            envelope.GroupId = message.Key;
                        }
                        else if (topic.StampConsumerGroupIdOnEnvelope)
                        {
                            envelope.GroupId = config.GroupId;
                        }

                        await receiver.ReceivedAsync(this, envelope);
                    }
                    catch (OperationCanceledException)
                    {
                        // we're done here!
                    }
                    catch (Exception e)
                    {
                        // Might be a poison pill message, advance past its specific offset so we don't
                        // get stuck re-consuming it (only possible when the consume itself succeeded).
                        if (result != null)
                        {
                            _committer.Complete(result.Topic, result.Partition.Value, result.Offset.Value);
                        }

                        logger.LogError(e, "Error trying to map Kafka message to a Wolverine envelope");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down
            }
            // The consumer is intentionally NOT closed here. StopAsync/Dispose flush pending offsets
            // first and then close the consumer, so a batch-mode commit isn't issued against an
            // already-closed consumer. See GH-3150.
        }, _cancellation.Token);
    }

    public ConsumerConfig Config { get; }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope.TopicName != null && envelope.PartitionId.HasValue)
        {
            _committer.Complete(envelope.TopicName, envelope.PartitionId.Value, envelope.Offset);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        // Really just a retry
        return _receiver.ReceivedAsync(this, envelope);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cancellation.Dispose();
        _consumer.SafeDispose();
        _runner.Dispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        await _runner;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

        // The consume loop has exited, so single-threaded access to the consumer is guaranteed: flush
        // pending/stored offsets first (GH-3150), then close cleanly.
        _committer.Flush();
        try
        {
            _consumer.Close();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error closing Kafka consumer on shutdown");
        }
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

            // Advance past the failed message's specific offset now that it's safely in the DLQ.
            if (envelope.TopicName != null && envelope.PartitionId.HasValue)
            {
                _committer.Complete(envelope.TopicName, envelope.PartitionId.Value, envelope.Offset);
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
        _cancellation.Cancel();
        _cancellation.Dispose();
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        _runner.Wait();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
        _committer.Flush();
        _consumer.SafeDispose();
        _runner.Dispose();
    }
}
