using Confluent.Kafka;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Kafka.Internals;

public class KafkaListener : IListener, IDisposable
{
    private readonly IConsumer<string, byte[]> _consumer;
    private CancellationTokenSource _cancellation = new();
    private readonly Task _runner;
    private readonly IReceiver _receiver;
    private readonly string? _messageTypeName;
    private readonly QualityOfService _qualityOfService;

    public KafkaListener(KafkaTopic topic, ConsumerConfig config,
        IConsumer<string, byte[]> consumer, IReceiver receiver,
        ILogger<KafkaListener> logger)
    {
        Address = topic.Uri;
        _consumer = consumer;
        var mapper = topic.Mapper;

        _messageTypeName = topic.MessageType?.ToMessageTypeName();

        Config = config;
        _receiver = receiver;

        _qualityOfService = Config.EnableAutoCommit.HasValue && !Config.EnableAutoCommit.Value
            ? QualityOfService.AtMostOnce
            : QualityOfService.AtLeastOnce;

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
                        envelope.MessageType ??= _messageTypeName;
                        envelope.GroupId = config.GroupId;

                        await receiver.ReceivedAsync(this, envelope);
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
                        
                        logger.LogError(e, "Error trying to map Kafka message to a Wolverine envelope");
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
        if (_qualityOfService == QualityOfService.AtLeastOnce)
        {
            try
            {
                _consumer.Commit();
            }
            catch (Exception)
            {

            }
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

    public void Dispose()
    {
        _consumer.SafeDispose();
        _runner.Dispose();
    }
}