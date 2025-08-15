using System.Threading.Tasks.Dataflow;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling.Matches;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports;

namespace Wolverine.ErrorHandling;

internal class CircuitBreakerTrackedExecutorFactory : IExecutorFactory
{
    private readonly IExecutorFactory _innerFactory;

    public CircuitBreakerTrackedExecutorFactory(CircuitBreaker breaker, IExecutorFactory innerFactory)
    {
        Breaker = breaker;
        _innerFactory = innerFactory;
    }

    public CircuitBreaker Breaker { get; }

    public IExecutor BuildFor(Type messageType)
    {
        var executor = _innerFactory.BuildFor(messageType);
        if (executor is Executor e)
        {
            return e.WrapWithMessageTracking(Breaker);
        }

        return executor;
    }

    public IExecutor BuildFor(Type messageType, Endpoint endpoint)
    {
        var executor = _innerFactory.BuildFor(messageType, endpoint);
        if (executor is Executor e)
        {
            return e.WrapWithMessageTracking(Breaker);
        }

        return executor;
    }
}

internal class CircuitBreakerWrappedMessageHandler : IMessageHandler
{
    private readonly IMessageHandler _inner;
    private readonly IMessageSuccessTracker _tracker;

    public CircuitBreakerWrappedMessageHandler(IMessageHandler inner, IMessageSuccessTracker tracker)
    {
        _inner = inner;
        _tracker = tracker;
    }

    public LogLevel ProcessingLogLevel => _inner.ProcessingLogLevel;

    public async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        try
        {
            await _inner.HandleAsync(context, cancellation);
            await _tracker.TagSuccessAsync();
        }
        catch (Exception e)
        {
            await _tracker.TagFailureAsync(e);
            throw;
        }
    }

    public Type MessageType => _inner.MessageType;

    public LogLevel ExecutionLogLevel => _inner.ExecutionLogLevel;
    public LogLevel SuccessLogLevel => _inner.SuccessLogLevel;

    public bool TelemetryEnabled => _inner.TelemetryEnabled;
}

internal interface IMessageSuccessTracker
{
    Task TagSuccessAsync();
    Task TagFailureAsync(Exception ex);
}

internal class CircuitBreaker : IAsyncDisposable, IMessageSuccessTracker
{
    private readonly BatchingChannel<object> _batching;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IListenerCircuit _circuit;
    private readonly List<Generation> _generations = new();
    private readonly IExceptionMatch _match;
    private readonly Block<object[]> _processingBlock;
    private readonly double _ratio;

    public CircuitBreaker(CircuitBreakerOptions options, IListenerCircuit circuit)
    {
        Options = options;
        _match = options.ToExceptionMatch();
        _circuit = circuit;

        _processingBlock = new Block<object[]>(processExceptionsAsync);
        _batching = new BatchingChannel<object>(options.SamplingPeriod, _processingBlock);

        GenerationPeriod = ((int)Math.Floor(Options.TrackingPeriod.TotalSeconds / 4)).Seconds();

        _ratio = Options.FailurePercentageThreshold / 100.0;
    }

    public CircuitBreakerOptions Options { get; }

    public TimeSpan GenerationPeriod { get; set; }

    public IReadOnlyList<Generation> CurrentGenerations => _generations;

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _processingBlock.Complete();
    }

    public Task TagSuccessAsync()
    {
        return _batching.PostAsync(this).AsTask();
    }

    public Task TagFailureAsync(Exception ex)
    {
        return _batching.PostAsync(ex).AsTask();
    }

    public bool ShouldStopProcessing()
    {
        var failures = _generations.Sum(x => x.Failures);
        var totals = _generations.Sum(x => x.Total);

        if (totals < Options.MinimumThreshold)
        {
            return false;
        }

        return failures / (double)totals >= _ratio;
    }

    private Task processExceptionsAsync(object[] tokens, CancellationToken _)
    {
        return ProcessExceptionsAsync(DateTimeOffset.UtcNow, tokens).AsTask();
    }

    public ValueTask ProcessExceptionsAsync(DateTimeOffset time, object[] tokens)
    {
        var failures = tokens.OfType<Exception>().Count(x => _match.Matches(x));

        return UpdateTotalsAsync(time, failures, tokens.Length);
    }

    public async ValueTask UpdateTotalsAsync(DateTimeOffset time, int failures, int total)
    {
        var generation = DetermineGeneration(time);
        generation.Failures += failures;
        generation.Total += total;

        if (failures > 0 && ShouldStopProcessing())
        {
            using var activity = WolverineTracing.ActivitySource.StartActivity(WolverineTracing.CircuitBreakerTripped);
            activity?.SetTag(WolverineTracing.EndpointAddress, _circuit.Endpoint.Uri);
            await _circuit.PauseAsync(Options.PauseTime);
        }
    }

    public Generation DetermineGeneration(DateTimeOffset now)
    {
        _generations.RemoveAll(x => x.IsExpired(now));
        if (!_generations.Any(x => x.IsActive(now)))
        {
            var generation = new Generation(now, this);
            _generations.Add(generation);

            return generation;
        }

        return _generations.Last();
    }

    public void Reset()
    {
        _generations.Clear();
    }

    internal class Generation
    {
        public Generation(DateTimeOffset start, CircuitBreaker parent)
        {
            Start = start;
            Expires = start.Add(parent.Options.TrackingPeriod);
            End = start.Add(parent.GenerationPeriod);
        }

        public DateTimeOffset Start { get; }
        public DateTimeOffset Expires { get; }

        public int Failures { get; set; }
        public int Total { get; set; }
        public DateTimeOffset End { get; set; }

        public bool IsExpired(DateTimeOffset now)
        {
            return now > Expires;
        }

        public bool IsActive(DateTimeOffset now)
        {
            return now >= Start && now < End;
        }

        public override string ToString()
        {
            return $"{nameof(Start)}: {Start}, {nameof(Expires)}: {Expires}, {nameof(End)}: {End}";
        }
    }
}