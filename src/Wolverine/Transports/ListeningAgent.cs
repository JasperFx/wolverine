using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Transports;

public interface IListenerCircuit
{
    ValueTask PauseAsync(TimeSpan pauseTime);
    ValueTask StartAsync();
    
    ListeningStatus Status { get; }
    Endpoint Endpoint { get; }
    int QueueCount { get; }
    
    void EnqueueDirectly(IEnumerable<Envelope> envelopes);
}

public interface IListeningAgent : IListenerCircuit
{
    Uri Uri { get; }

    ValueTask StopAndDrainAsync();

    ValueTask MarkAsTooBusyAndStopReceivingAsync();
}

internal class ListeningAgent : IAsyncDisposable, IDisposable, IListeningAgent
{
    private readonly IWolverineRuntime _runtime;
    private IListener? _listener;
    private IReceiver? _receiver;
    private Restarter? _restarter;
    private readonly ILogger _logger;
    private readonly HandlerPipeline _pipeline;
    private readonly CircuitBreaker? _circuitBreaker;
    private readonly BackPressureAgent? _backPressureAgent;

    public ListeningAgent(Endpoint endpoint, WolverineRuntime runtime)
    {
        Endpoint = endpoint;
        _runtime = runtime;
        Uri = endpoint.Uri;
        _logger = runtime.Logger;

        if (endpoint.CircuitBreakerOptions != null)
        {
            _circuitBreaker = new CircuitBreaker(endpoint.CircuitBreakerOptions, this);
            _pipeline = new HandlerPipeline(runtime,
                new CircuitBreakerTrackedExecutorFactory(_circuitBreaker,
                    new CircuitBreakerTrackedExecutorFactory(_circuitBreaker, runtime)));
        }
        else
        {
            _pipeline = new HandlerPipeline(runtime, runtime);
        }

        if (endpoint.ShouldEnforceBackPressure())
        {
            _backPressureAgent = new BackPressureAgent(this, endpoint);
            _backPressureAgent.Start();
        }
    }

    public int QueueCount => _receiver is ILocalQueue q ? q.QueueCount : 0;

    public void EnqueueDirectly(IEnumerable<Envelope> envelopes)
    {
        if (_receiver is ILocalQueue queue)
        {
            var uniqueNodeId = _runtime.Advanced.UniqueNodeId;
            foreach (var envelope in envelopes)
            {
                envelope.OwnerId = uniqueNodeId;
                queue.Enqueue(envelope);
            }
        }
        else
        {
            throw new InvalidOperationException("There is no active, local queue for this listening endpoint at " +
                                                Endpoint.Uri);
        }
    }

    public Endpoint Endpoint { get; }

    public Uri Uri { get; }

    public ListeningStatus Status { get; private set; } = ListeningStatus.Stopped;

    public async ValueTask StopAndDrainAsync()
    {
        if (Status == ListeningStatus.Stopped) return;
        if (_listener == null) return;

        await _listener.StopAsync();
        await _receiver!.DrainAsync();

        await _listener.DisposeAsync();
        _receiver?.Dispose();

        _listener = null;
        _receiver = null;

        Status = ListeningStatus.Stopped;
        _runtime.ListenerTracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, Status));

        _logger.LogInformation("Stopped message listener at {Uri}", Uri);
    }

    public async ValueTask StartAsync()
    {
        if (Status == ListeningStatus.Accepting) return;

        _receiver ??= await buildReceiverAsync();

        _listener = await Endpoint.BuildListenerAsync(_runtime, _receiver);

        Status = ListeningStatus.Accepting;
        _runtime.ListenerTracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, Status));

        _logger.LogInformation("Started message listening at {Uri}", Uri);

    }

    public async ValueTask PauseAsync(TimeSpan pauseTime)
    {
        await StopAndDrainAsync();

        _circuitBreaker?.Reset();

        _logger.LogInformation("Pausing message listening at {Uri}", Uri);
        // TODO -- publish through the ListenerTracker here
        _restarter = new Restarter(this, pauseTime);

    }

    public async ValueTask MarkAsTooBusyAndStopReceivingAsync()
    {
        if (Status != ListeningStatus.Accepting || _listener == null) return;
        await _listener.StopAsync();
        await _listener.DisposeAsync();
        _listener = null;
        
        Status = ListeningStatus.TooBusy;
        _runtime.ListenerTracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, Status));

        _logger.LogInformation("Marked listener at {Uri} as too busy and stopped receiving", Uri);
    }

    private async ValueTask<IReceiver> buildReceiverAsync()
    {
        switch (Endpoint.Mode)
        {
            case EndpointMode.Durable:
                var receiver = new DurableReceiver(Endpoint, _runtime, _pipeline);
                await receiver.ClearInFlightIncomingAsync();
                return receiver;

            case EndpointMode.Inline:
                return new InlineReceiver(_runtime, _pipeline);

            case EndpointMode.BufferedInMemory:
                return new BufferedReceiver(Endpoint, _runtime, _pipeline);

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public void Dispose()
    {
        _receiver?.Dispose();
        _circuitBreaker?.SafeDispose();
        _backPressureAgent?.SafeDispose();
    }

    public async ValueTask DisposeAsync()
    {
        _restarter?.SafeDispose();
        _backPressureAgent?.SafeDispose();

        if (_listener != null) await _listener.DisposeAsync();
        _receiver?.Dispose();

        _circuitBreaker?.SafeDispose();

        _listener = null;
        _receiver = null;
    }
}

internal class Restarter : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly Task<Task> _task;

    public Restarter(IListenerCircuit parent, TimeSpan timeSpan)
    {
        _cancellation = new CancellationTokenSource();
        _task = Task.Delay(timeSpan, _cancellation.Token)
            .ContinueWith(async _ =>
            {
                if (_cancellation.IsCancellationRequested) return;
                await parent.StartAsync();
            }, TaskScheduler.Default);
    }


    public void Dispose()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
    }
}