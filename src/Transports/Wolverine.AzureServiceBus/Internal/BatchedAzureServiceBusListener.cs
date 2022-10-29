using Azure.Messaging.ServiceBus;
using Baseline;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus.Internal;

public class BatchedAzureServiceBusListener : IListener
{
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly IReceiver _wolverineReceiver;
    private readonly ServiceBusReceiver _receiver;
    private readonly IIncomingMapper<ServiceBusReceivedMessage> _mapper;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _task;

    public BatchedAzureServiceBusListener(AzureServiceBusEndpoint endpoint, IReceiver wolverineReceiver, ServiceBusReceiver receiver, IIncomingMapper<ServiceBusReceivedMessage> mapper)
    {
        _endpoint = endpoint;
        _wolverineReceiver = wolverineReceiver;
        _receiver = receiver;
        _mapper = mapper;
        
        _task = Task.Run(listenForMessages, _cancellation.Token);
    }

    private async Task listenForMessages()
    {
        while (!_cancellation.Token.IsCancellationRequested)
        {
            // TODO -- harden like crazy

            var messages = await _receiver
                .ReceiveMessagesAsync(_endpoint.MaximumMessagesToReceive,
                    _endpoint.MaximumWaitTime, _cancellation.Token);

            if (messages.Any())
            {
                var envelopes = messages.Select(m =>
                {
                    var envelope = new AzureServiceBusEnvelope(m);
                    _mapper.MapIncomingToEnvelope(envelope, m);

                    return (Envelope)envelope;
                }).ToArray();

                await _wolverineReceiver.ReceivedAsync(this, envelopes);
            }

            // TODO -- harden all of this
            // TODO -- put a cooldown here? 

        }
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            var task = _receiver.CompleteMessageAsync(e.AzureMessage);
            return new ValueTask(task);
        }
        
        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            var task = _receiver.DeferMessageAsync(e.AzureMessage,
                new Dictionary<string, object>{{EnvelopeConstants.AttemptsKey, envelope.Attempts + 1}});
            return new ValueTask(task);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
        return _receiver.DisposeAsync();
    }

    public Uri Address => _endpoint.Uri;
    public ValueTask StopAsync()
    {
        _cancellation.Cancel();
        return new ValueTask(_receiver.CloseAsync());
    }
}