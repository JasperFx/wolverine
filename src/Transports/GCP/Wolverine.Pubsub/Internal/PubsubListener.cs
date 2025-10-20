using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public class PubsubListener : IListener
{
    private readonly IReceiver _receiver;

    private readonly IPubsubEnvelopeMapper _mapper;

    private readonly CancellationTokenSource _cancellation = new();

    private readonly Task _task;

    private SubscriberClient? _subscriber;
    // TODO -- add native DLQ mechanics

    public PubsubListener(
        PubsubSubscription subscription, 
        IWolverineRuntime runtime, 
        IReceiver receiver)
    {
        _receiver = receiver;
        _mapper = subscription.BuildMapper(runtime);
        Address = subscription.Uri;

        _task = Task.Run(async () =>
        {
            var builder = new SubscriberClientBuilder
            {
                EmulatorDetection = subscription.Topic.Parent.EmulatorDetection,
                SubscriptionName = subscription.SubscriptionName
            };

            _subscriber = builder.Build();

            await _subscriber.StartAsync(async (msg, cancellation) =>
            {
                var envelope = new PubsubEnvelope{AckId = msg.MessageId};
                
                // TODO -- harden this!
                _mapper.MapIncomingToEnvelope(envelope, msg);
                await _receiver.ReceivedAsync(this, envelope);
                
                return envelope.Reply;
            });
        }, _cancellation.Token);
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;
    
    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is PubsubEnvelope e) e.Reply = SubscriberClient.Reply.Ack;
        return new ValueTask();
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is PubsubEnvelope e) e.Reply = SubscriberClient.Reply.Nack;
        return new ValueTask();
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscriber != null)
        {
            await _subscriber.StopAsync(_cancellation.Token);
        }

        await _task;
        await _cancellation.CancelAsync();
        _task.SafeDispose();
    }

    public Uri Address { get; }
    public ValueTask StopAsync()
    {
        return DisposeAsync();
    }
}