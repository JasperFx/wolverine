using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Transports;

public interface IListenerCircuit
{
    ListeningStatus Status { get; }
    Endpoint Endpoint { get; }
    int QueueCount { get; }
    ValueTask PauseAsync(TimeSpan pauseTime);
    ValueTask StartAsync();

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
    private readonly BackPressureAgent? _backPressureAgent;
    private readonly CircuitBreaker? _circuitBreaker;
    private readonly ILogger _logger;
    private readonly HandlerPipeline _pipeline;
    private readonly IWolverineRuntime _runtime;
    private IListener? _listener;
    private IReceiver? _receiver;
    private Restarter? _restarter;

    public ListeningAgent(Endpoint endpoint, WolverineRuntime runtime)
    {
        Endpoint = endpoint;
        _runtime = runtime;
        Uri = endpoint.Uri;
        _logger = runtime.LoggerFactory.CreateLogger<ListeningAgent>();

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

    public async ValueTask DisposeAsync()
    {
        _restarter?.SafeDispose();
        _backPressureAgent?.SafeDispose();

        if (_listener != null)
        {
            await _listener.DisposeAsync();
        }

        _receiver?.Dispose();

        _circuitBreaker?.SafeDispose();

        _listener = null;
        _receiver = null;
    }

    public void Dispose()
    {
        _receiver?.Dispose();
        _circuitBreaker?.SafeDispose();
        _backPressureAgent?.SafeDispose();
    }

    public int QueueCount => _receiver is ILocalQueue q ? q.QueueCount : 0;

    public void EnqueueDirectly(IEnumerable<Envelope> envelopes)
    {
        if (_receiver is ILocalQueue queue)
        {
            var uniqueNodeId = _runtime.DurabilitySettings.UniqueNodeId;
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
        if (Status == ListeningStatus.Stopped)
        {
            return;
        }

        if (_listener == null)
        {
            return;
        }

        try
        {
            await _listener.StopAsync();
            await _receiver!.DrainAsync();

            await _listener.DisposeAsync();
            _receiver?.Dispose();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to stop and drain the listener for {Uri}", Uri);
        }

        _listener = null;
        _receiver = null;

        Status = ListeningStatus.Stopped;
        _runtime.ListenerTracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, Status));

        _logger.LogInformation("Stopped message listener at {Uri}", Uri);
    }

    public async ValueTask StartAsync()
    {
        if (Status == ListeningStatus.Accepting)
        {
            return;
        }

        _receiver ??= await buildReceiverAsync();

        _listener = await Endpoint.BuildListenerAsync(_runtime, _receiver);

        Status = ListeningStatus.Accepting;
        _runtime.ListenerTracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, Status));

        _logger.LogInformation("Started message listening at {Uri}", Uri);
    }

    public async ValueTask PauseAsync(TimeSpan pauseTime)
    {
        try
        {
            await StopAndDrainAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to drain outstanding messages in the listener for {Uri}", Uri);
        }

        _circuitBreaker?.Reset();

        _logger.LogInformation("Pausing message listening at {Uri}", Uri);
        _runtime.ListenerTracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, ListeningStatus.Stopped));
        _restarter = new Restarter(this, pauseTime);
    }

    public async ValueTask MarkAsTooBusyAndStopReceivingAsync()
    {
        if (Status != ListeningStatus.Accepting || _listener == null)
        {
            return;
        }

        try
        {
            await _listener.StopAsync();
            await _listener.DisposeAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to cleanly stop the listener for {Uri}", Uri);
        }

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
                if (_cancellation.IsCancellationRequested)
                {
                    return;
                }

                await parent.StartAsync();
            }, TaskScheduler.Default);
    }


    public void Dispose()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
    }
}