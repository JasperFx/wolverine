using System.Diagnostics;
using System.Runtime.CompilerServices;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Runtime.Handlers;

public enum InvokeResult
{
    /// <summary>
    /// The message is successful
    /// </summary>
    Success,
    
    /// <summary>
    /// The message should be retried
    /// </summary>
    TryAgain,
    
    /// <summary>
    /// The message should not be retried
    /// </summary>
    Stop
}

public interface IExecutor : IMessageInvoker
{
    Task<IContinuation> ExecuteAsync(MessageContext context, CancellationToken cancellation);
    Task<InvokeResult> InvokeAsync(MessageContext context, CancellationToken cancellation);
    Task InvokeInlineAsync(Envelope envelope, CancellationToken cancellation);
}

internal class Executor : IExecutor
{
    public const int MessageSucceededEventId = 104;
    public const int MessageFailedEventId = 105;
    public const int ExecutionStartedEventId = 102;
    public const int ExecutionFinishedEventId = 103;

    private readonly ObjectPool<MessageContext> _contextPool;
    private readonly Action<ILogger, string, string, Guid, Exception?> _executionFinished;
    private readonly Action<ILogger, string, string, Guid, Exception?> _executionStarted;
    private readonly ILogger _logger;

    private readonly Action<ILogger, string, Guid, string, Exception> _messageFailed;
    private readonly Action<ILogger, string, Guid, string, Exception?> _messageSucceeded;

    private readonly string _messageTypeName;
    private readonly FailureRuleCollection _rules;
    private readonly TimeSpan _timeout;
    private readonly IMessageTracker _tracker;
    private readonly IWolverineRuntime? _runtime;

    /// <summary>
    /// When <see langword="true"/>, the executor publishes the in-flight <see cref="MessageContext"/>
    /// through <see cref="MessageContext.Current"/> for the duration of each invocation, so
    /// service-located <see cref="IMessageContext"/> / <see cref="IMessageBus"/> see the same
    /// instance the handler itself received. Set only when the
    /// chain's compiled code resolves at least one dependency via service location, so chains
    /// that don't service-locate pay zero <see cref="System.Threading.AsyncLocal{T}"/> overhead
    /// per message. See issue #2583.
    /// </summary>
    private bool _capturesContextForServiceLocation;

    public Executor(ObjectPool<MessageContext> contextPool, IWolverineRuntime runtime, IMessageHandler handler,
        FailureRuleCollection rules, TimeSpan timeout)
        : this(contextPool, runtime.LoggerFactory.CreateLogger(handler.MessageType), handler, runtime.MessageTracking, rules, timeout)
    {
        _runtime = runtime;
    }

    public Executor(ObjectPool<MessageContext> contextPool, ILogger logger, IMessageHandler handler,
        IMessageTracker tracker, FailureRuleCollection rules, TimeSpan timeout)
    {
        _contextPool = contextPool;
        Handler = handler;
        _tracker = tracker;
        _rules = rules;
        _timeout = timeout;

        _logger = logger;

        _messageSucceeded =
            LoggerMessage.Define<string, Guid, string>(handler.SuccessLogLevel, MessageSucceededEventId,
                "Successfully processed message {Name}#{envelope} from {ReplyUri}");

        _messageFailed = LoggerMessage.Define<string, Guid, string>(LogLevel.Error, MessageFailedEventId,
            "Failed to process message {Name}#{envelope} from {ReplyUri}");

        _messageTypeName = handler.MessageType.ToMessageTypeName();

        _executionStarted = LoggerMessage.Define<string, string, Guid>(handler.ProcessingLogLevel, ExecutionStartedEventId,
            "{CorrelationId}: Started processing {Name}#{Id}");

        _executionFinished = LoggerMessage.Define<string, string, Guid>(handler.ProcessingLogLevel, ExecutionFinishedEventId,
            "{CorrelationId}: Finished processing {Name}#{Id}");
    }

    public IMessageHandler Handler { get; }

    /// <summary>
    /// Acquire an envelope for an <c>InvokeAsync</c>-style invocation. Wraps
    /// <see cref="WolverineRuntime.AcquireInternalEnvelope"/> with the
    /// <see cref="Envelope.Message"/>-stamping ritual, and falls back to
    /// direct allocation when no runtime is wired (the test-only constructor
    /// path). See wolverine#2726.
    /// </summary>
    private (Envelope envelope, bool fromPool) AcquireInternalEnvelope(object message)
    {
        // Helpers live on the concrete WolverineRuntime, not IWolverineRuntime,
        // so an internal hot-path detail doesn't leak into the public-ish
        // interface. In practice _runtime is always WolverineRuntime; the
        // test-only constructor path leaves _runtime null and we fall back
        // to direct allocation below.
        if (_runtime is not WolverineRuntime runtime)
        {
            return (new Envelope(message), false);
        }

        var envelope = runtime.AcquireInternalEnvelope(out var fromPool);
        // The Message setter also stamps MessageType — that's what the
        // Envelope(object) constructor does, so the pool path matches the
        // direct-allocation path's post-condition.
        envelope.Message = message;
        return (envelope, fromPool);
    }

    private void ReleaseInternalEnvelope(Envelope envelope, bool fromPool)
    {
        if (_runtime is WolverineRuntime runtime)
        {
            runtime.ReleaseInternalEnvelope(envelope, fromPool);
        }
    }

    public async Task InvokeInlineAsync(Envelope envelope, CancellationToken cancellation)
    {
        using var activity = Handler.TelemetryEnabled ? WolverineTracing.StartExecuting(envelope) : null;

        _tracker.ExecutionStarted(envelope);

        var context = _contextPool.Get();
        context.ReadEnvelope(envelope, InvocationCallback.Instance);

        envelope.Attempts = 1;

        try
        {
            while (await InvokeAsync(context, cancellation).ConfigureAwait(false) == InvokeResult.TryAgain)
            {
                envelope.Attempts++;
            }

            await context.FlushOutgoingMessagesAsync().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _tracker.ExecutionFinished(envelope);
        }
        catch (Exception e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            _tracker.ExecutionFinished(envelope, e);
            throw;
        }
        finally
        {
            _contextPool.Return(context);
            activity?.Stop();
        }
    }

    public async Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        // Pool when ActiveSession is null (production hot path); allocate
        // fresh when tracking is on, so EnvelopeRecord captures aren't
        // corrupted by a recycle. See wolverine#2726.
        var (envelope, fromPool) = AcquireInternalEnvelope(message);
        envelope.ReplyUri = TransportConstants.RepliesUri;
        envelope.ReplyRequested = typeof(T).ToMessageTypeName();
        envelope.ResponseType = typeof(T);
        envelope.TenantId = options?.TenantId ?? bus.TenantId;
        envelope.DoNotCascadeResponse = true;

        options?.Override(envelope);

        bus.TrackEnvelopeCorrelation(envelope, Activity.Current);

        try
        {
            await InvokeInlineAsync(envelope, cancellation).ConfigureAwait(false);

            if (envelope.Response == null)
            {
                return default!;
            }

            return (T)envelope.Response;
        }
        finally
        {
            ReleaseInternalEnvelope(envelope, fromPool);
        }
    }

    public async Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        var (envelope, fromPool) = AcquireInternalEnvelope(message);
        envelope.TenantId = options?.TenantId ?? bus.TenantId;

        options?.Override(envelope);

        bus.TrackEnvelopeCorrelation(envelope, Activity.Current);
        try
        {
            await InvokeInlineAsync(envelope, cancellation).ConfigureAwait(false);
        }
        finally
        {
            ReleaseInternalEnvelope(envelope, fromPool);
        }
    }

    public async Task<IContinuation> ExecuteAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope;
        _tracker.ExecutionStarted(envelope!);
        _executionStarted(_logger, envelope!.CorrelationId!, _messageTypeName, envelope.Id, null);

        envelope.Attempts++;

        using var timeout = new CancellationTokenSource(_timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellation);

        // Publish the in-flight context for service location only when the chain's codegen
        // reported at least one service-located dependency. Pure-codegen chains skip the
        // AsyncLocal touch entirely. See #2583 / ServiceLocationAwareExecutor docs.
        var previousAmbient = _capturesContextForServiceLocation ? MessageContext.Current : null;
        if (_capturesContextForServiceLocation)
        {
            MessageContext.Current = context;
        }

        try
        {
            await Handler.HandleAsync(context, combined.Token).ConfigureAwait(false);

            if (context.Envelope!.ReplyRequested.IsNotEmpty())
            {
                await context.AssertAnyRequiredResponseWasGenerated().ConfigureAwait(false);
            }

            Activity.Current?.SetStatus(ActivityStatusCode.Ok);

            _messageSucceeded(_logger, _messageTypeName, envelope.Id,
                envelope.Destination!.ToString(), null);

            _tracker.ExecutionFinished(envelope);

            return MessageSucceededContinuation.Instance;
        }
        catch (Exception e)
        {
            _messageFailed(_logger, _messageTypeName, envelope.Id, envelope.Destination!.ToString(), e);

            _tracker
                .ExecutionFinished(envelope, e); // Need to do this to make the MessageHistory complete

            await context.ClearAllAsync().ConfigureAwait(false);

            Activity.Current?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            return _rules.DetermineExecutionContinuation(e, envelope);
        }
        finally
        {
            if (_capturesContextForServiceLocation)
            {
                MessageContext.Current = previousAmbient;
            }
            _executionFinished(_logger, envelope.CorrelationId!, _messageTypeName, envelope.Id, null);
        }
    }

    public async Task<InvokeResult> InvokeAsync(MessageContext context, CancellationToken cancellation)
    {
        if (context.Envelope == null)
        {
            throw new ArgumentOutOfRangeException(nameof(context.Envelope));
        }

        var previousAmbient = _capturesContextForServiceLocation ? MessageContext.Current : null;
        if (_capturesContextForServiceLocation)
        {
            MessageContext.Current = context;
        }

        try
        {
            await Handler.HandleAsync(context, cancellation).ConfigureAwait(false);
            if (context.Envelope.ReplyRequested.IsNotEmpty())
            {
                await context.AssertAnyRequiredResponseWasGenerated().ConfigureAwait(false);
            }
            
            return InvokeResult.Success;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Invocation of {Message} failed!", context.Envelope.Message);

            var retry = _rules.TryFindInlineContinuation(e, context.Envelope);
            if (retry == null)
            {
                throw;
            }

            return await retry
                .ExecuteInlineAsync(context, context.Runtime, DateTimeOffset.UtcNow, Activity.Current, cancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            if (_capturesContextForServiceLocation)
            {
                MessageContext.Current = previousAmbient;
            }
        }
    }

    public IAsyncEnumerable<T> StreamAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        DeliveryOptions? options = null)
    {
        var envelope = new Envelope(message)
        {
            ResponseType = typeof(IAsyncEnumerable<T>),
            TenantId = options?.TenantId ?? bus.TenantId,
            DoNotCascadeResponse = true
        };

        options?.Override(envelope);
        bus.TrackEnvelopeCorrelation(envelope, Activity.Current);

        return StreamCoreAsync<T>(envelope, cancellation);
    }

    private async IAsyncEnumerable<T> StreamCoreAsync<T>(Envelope envelope,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        using var activity = Handler.TelemetryEnabled ? WolverineTracing.StartStreaming(envelope) : null;

        _tracker.ExecutionStarted(envelope);

        var context = _contextPool.Get();
        context.ReadEnvelope(envelope, InvocationCallback.Instance);
        envelope.Attempts = 1;

        IAsyncEnumerable<T>? stream = null;

        try
        {
            await InvokeAsync(context, cancellation).ConfigureAwait(false);

            await context.FlushOutgoingMessagesAsync().ConfigureAwait(false);
            stream = envelope.Response as IAsyncEnumerable<T>;
            activity?.AddEvent(new ActivityEvent(WolverineTracing.StreamingStarted));
        }
        catch (Exception e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            _tracker.ExecutionFinished(envelope, e);
            _contextPool.Return(context);
            throw;
        }

        if (stream == null)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            _tracker.ExecutionFinished(envelope);
            _contextPool.Return(context);
            yield break;
        }

        await using var enumerator = stream.GetAsyncEnumerator(cancellation);
        try
        {
            while (true)
            {
                T current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        activity?.AddEvent(new ActivityEvent(WolverineTracing.StreamingCompleted));
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        _tracker.ExecutionFinished(envelope);
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception e)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
                    _tracker.ExecutionFinished(envelope, e);
                    throw;
                }

                yield return current;
            }
        }
        finally
        {
            _contextPool.Return(context);
        }
    }

    internal Executor WrapWithMessageTracking(IMessageSuccessTracker tracker)
    {
        return new Executor(_contextPool, _logger, new CircuitBreakerWrappedMessageHandler(Handler, tracker), _tracker,
            _rules, _timeout);
    }

    /// <summary>
    /// Set when the chain's compiled code is known to resolve a
    /// dependency via service location. Toggles the per-invocation
    /// <see cref="MessageContext.Current"/> publish/restore so service-located
    /// <see cref="IMessageContext"/> / <see cref="IMessageBus"/> see the same instance the
    /// handler received. See issue #2583.
    /// </summary>
    internal void EnableServiceLocationContextCapture() => _capturesContextForServiceLocation = true;

    public static IExecutor Build(IWolverineRuntime runtime, ObjectPool<MessageContext> contextPool,
        HandlerGraph handlerGraph, Type messageType)
    {
        var handler = handlerGraph.HandlerFor(messageType);
        if (handler == null )
        {
            var batching = runtime.Options.BatchDefinitions.FirstOrDefault(x => x.ElementType == messageType);
            if (batching != null)
            {
                handler = batching.BuildHandler((WolverineRuntime)runtime);
            }
        }

        if (handler == null)
        {
            return new NoHandlerExecutor(messageType, (WolverineRuntime)runtime);
        }

        var chain = (handler as MessageHandler)?.Chain;
        var timeoutSpan = chain?.DetermineMessageTimeout(runtime.Options) ?? 5.Seconds();
        var rules = chain?.Failures.CombineRules(handlerGraph.Failures) ?? handlerGraph.Failures;

        if (runtime.Options.InvokeTracing == InvokeTracingMode.Full)
        {
            var tracingExecutor = new TracingExecutor(contextPool, runtime, handler, rules, timeoutSpan);
            if (chain?.UsesServiceLocation == true)
            {
                tracingExecutor.EnableServiceLocationContextCapture();
            }
            return tracingExecutor;
        }

        var executor = new Executor(contextPool, runtime, handler, rules, timeoutSpan);
        if (chain?.UsesServiceLocation == true)
        {
            executor.EnableServiceLocationContextCapture();
        }
        return executor;
    }

    public static IExecutor Build(IWolverineRuntime runtime, ObjectPool<MessageContext> contextPool,
        HandlerGraph handlerGraph, IMessageHandler handler, IMessageTracker tracker)
    {
        var chain = (handler as MessageHandler)?.Chain;
        var timeoutSpan = chain?.DetermineMessageTimeout(runtime.Options) ?? 5.Seconds();
        var rules = chain?.Failures.CombineRules(handlerGraph.Failures) ?? handlerGraph.Failures;

        var logger = runtime.LoggerFactory.CreateLogger(handler.MessageType);

        if (runtime.Options.InvokeTracing == InvokeTracingMode.Full)
        {
            var tracingExecutor = new TracingExecutor(contextPool, logger, handler, tracker, rules, timeoutSpan);
            if (chain?.UsesServiceLocation == true)
            {
                tracingExecutor.EnableServiceLocationContextCapture();
            }
            return tracingExecutor;
        }

        var executor = new Executor(contextPool, logger, handler, tracker, rules, timeoutSpan);
        if (chain?.UsesServiceLocation == true)
        {
            executor.EnableServiceLocationContextCapture();
        }
        return executor;
    }
}