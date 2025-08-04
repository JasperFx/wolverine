using System.Diagnostics;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Stub;

internal class StubEndpoint : Endpoint, ISendingAgent, ISender, IListener
{
    private readonly StubTransport _stubTransport;

    // ReSharper disable once CollectionNeverQueried.Global
    public readonly IList<StubChannelCallback> Callbacks = new List<StubChannelCallback>();

    // ReSharper disable once CollectionNeverQueried.Global
    public readonly IList<Envelope> Sent = new List<Envelope>();
    private IMessageTracker? _logger;
    private IHandlerPipeline? _pipeline;

    public StubEndpoint(string queueName, StubTransport stubTransport) : base($"stub://{queueName}".ToUri(),
        EndpointRole.Application)
    {
        _stubTransport = stubTransport;
        Agent = this;
        EndpointName = queueName;
    }

    public ValueTask StopAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public IHandlerPipeline? Pipeline => _pipeline;

    ValueTask IChannelCallback.CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    ValueTask IChannelCallback.DeferAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    Uri IListener.Address => Destination;

    public ValueTask SendAsync(Envelope envelope)
    {
        Sent.Add(envelope);
        if (_pipeline != null)
        {
            return new ValueTask(_pipeline.InvokeAsync(envelope, new StubChannelCallback(this, envelope)));
        }

        return ValueTask.CompletedTask;
    }

    public Task<bool> PingAsync()
    {
        return Task.FromResult(true);
    }

    public Endpoint Endpoint => this;
    public bool Latched => false;

    public bool IsDurable => Mode == EndpointMode.Durable;

    public Uri Destination => Uri;

    Uri? ISendingAgent.ReplyUri
    {
        get => _stubTransport.ReplyEndpoint()?.Uri;
        set => Debug.WriteLine(value);
    }

    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        envelope.ReplyUri ??= Destination;

        var callback = new StubChannelCallback(this, envelope);
        Callbacks.Add(callback);

        Sent.Add(envelope);

        _logger?.Sent(envelope);

        if (_pipeline != null)
        {
            return new ValueTask(_pipeline.InvokeAsync(envelope, callback));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        return EnqueueOutgoingAsync(envelope);
    }

    public bool SupportsNativeScheduledSend => true;

    public void Dispose()
    {
    }

    public void Start(IHandlerPipeline pipeline, IMessageTracker logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return ValueTask.FromResult<IListener>(this);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return this;
    }
}