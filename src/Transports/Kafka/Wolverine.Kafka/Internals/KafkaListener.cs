using Confluent.Kafka;
using JasperFx.Core;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

internal class KafkaListener : IListener, IDisposable
{
    private readonly IConsumer<string,string> _consumer;
    private CancellationTokenSource _cancellation = new();
    private readonly Task _runner;
    private readonly ConsumerConfig _config;
    private readonly IReceiver _receiver;

    public KafkaListener(KafkaTopic topic, ConsumerConfig config, IReceiver receiver)
    {
        Address = topic.Uri;
        _consumer = new ConsumerBuilder<string, string>(config).Build();
        var mapper = topic.Mapper;

        _config = config;
        _receiver = receiver;

        _runner = Task.Run(async () =>
        {
            _consumer.Subscribe(topic.TopicName);
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    // TODO -- watch that this isn't EnableAutoCommit = false
                    // TODO -- wrap try catch around this
                    var result = _consumer.Consume(_cancellation.Token);
                    var message = result.Message;

                    var envelope = mapper.CreateEnvelope(result.Topic, message);
  
                    await receiver.ReceivedAsync(this, envelope);
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

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (_config.EnableAutoCommit != null)
        {
            _consumer.Commit();
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