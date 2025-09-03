using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Interop;
using Wolverine.Transports;

namespace Wolverine.SignalR.Internals;

public abstract class WolverineHub : Hub, IListener
{
    private readonly IWolverineRuntime _runtime;
    private readonly SignalREndpoint _endpoint;
    private readonly CloudEventsMapper _mapper;
    private IHandlerPipeline? _pipeline;
    private Uri _address;

    protected WolverineHub(IWolverineRuntime runtime)
    {
        _runtime = runtime;
        _endpoint = runtime.Options.SignalRTransport().HubEndpoints[GetType()];

        _mapper = new CloudEventsMapper(runtime.Services.GetRequiredService<HandlerGraph>(), _endpoint.JsonOptions);
        _endpoint.Hub = this;
    }
    
    internal IReceiver? Receiver { get; set; }

    public async Task Receive(string json)
    {
        if (Receiver == null) return;
        
        var envelope = new Envelope();
        _mapper.MapIncoming(envelope, json);
        await Receiver.ReceivedAsync(this, envelope);
    }

    IHandlerPipeline? IChannelCallback.Pipeline => _pipeline;

    ValueTask IChannelCallback.CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    ValueTask IChannelCallback.DeferAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Receiver = null;
        return new ValueTask();
    }

    Uri IListener.Address => _endpoint.Uri;

    ValueTask IListener.StopAsync()
    {
        return new ValueTask();
    }
}