using System;
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

public interface IListeningAgent
{
    Uri Uri { get; }
    ListeningStatus Status { get; }
    Endpoint Endpoint { get; }
    ValueTask StopAsync();
    ValueTask StartAsync();
    ValueTask PauseAsync(TimeSpan pauseTime);
}

internal class ListeningAgent : IAsyncDisposable, IDisposable, IListeningAgent
{
    private readonly WolverineRuntime _runtime;
    private IListener? _listener;
    private IReceiver? _receiver;
    private Restarter? _restarter;
    private readonly ILogger _logger;
    private readonly HandlerPipeline _pipeline;
    private readonly CircuitBreaker? _circuitBreaker;

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
    }

    public Endpoint Endpoint { get; }

    public Uri Uri { get; }

    public ListeningStatus Status { get; private set; } = ListeningStatus.Stopped;

    public async ValueTask StopAsync()
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
        _runtime.ListenerTracker.Publish(new ListenerState(Uri, Endpoint.Name, Status));

        _logger.LogInformation("Stopped message listener at {Uri}", Uri);
    }

    public async ValueTask StartAsync()
    {
        if (Status == ListeningStatus.Accepting) return;

        _receiver = await buildReceiver();

        _listener = Endpoint.BuildListener(_runtime, _receiver);

        Status = ListeningStatus.Accepting;
        _runtime.ListenerTracker.Publish(new ListenerState(Uri, Endpoint.Name, Status));

        _logger.LogInformation("Started message listening at {Uri}", Uri);

    }

    public async ValueTask PauseAsync(TimeSpan pauseTime)
    {
        await StopAsync();

        _circuitBreaker?.Reset();

        _logger.LogInformation("Pausing message listening at {Uri}", Uri);

        _restarter = new Restarter(this, pauseTime);

    }

    private async ValueTask<IReceiver> buildReceiver()
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
    }

    public async ValueTask DisposeAsync()
    {
        _restarter?.SafeDispose();

        if (_listener != null) await _listener.DisposeAsync();
        _receiver?.Dispose();

        _circuitBreaker?.SafeDispose();

        _listener = null;
        _receiver = null;
    }

    internal class Restarter : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Task<Task> _task;

        public Restarter(ListeningAgent parent, TimeSpan timeSpan)
        {
            _cancellation = new CancellationTokenSource();
            _task = Task.Delay(timeSpan, _cancellation.Token)
                .ContinueWith(async t =>
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
}
