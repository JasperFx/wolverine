using System.Diagnostics;
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
    Success,
    TryAgain
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

    public Executor(ObjectPool<MessageContext> contextPool, IWolverineRuntime runtime, IMessageHandler handler,
        FailureRuleCollection rules, TimeSpan timeout)
        : this(contextPool, runtime.LoggerFactory.CreateLogger(handler.MessageType), handler, runtime.MessageTracking, rules, timeout)
    {
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
            LoggerMessage.Define<string, Guid, string>(handler.ExecutionLogLevel, MessageSucceededEventId,
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

    public async Task InvokeInlineAsync(Envelope envelope, CancellationToken cancellation)
    {
        using var activity = Handler.TelemetryEnabled ? WolverineTracing.StartExecuting(envelope) : null;

        _tracker.ExecutionStarted(envelope);

        var context = _contextPool.Get();
        context.ReadEnvelope(envelope, InvocationCallback.Instance);

        envelope.Attempts = 1;

        try
        {
            while (await InvokeAsync(context, cancellation) == InvokeResult.TryAgain)
            {
                envelope.Attempts++;
            }

            await context.FlushOutgoingMessagesAsync();
            activity?.SetStatus(ActivityStatusCode.Ok);
            _tracker.ExecutionFinished(envelope);
        }
        catch (Exception e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            _tracker.ExecutionFinished(envelope, e);
            _logger.LogError(e, "Inline invocation of {Message} failed", envelope.Message);
            throw;
        }
        finally
        {
            _contextPool.Return(context);
            activity?.Stop();
        }
    }

    public async Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, string? tenantId = null)
    {
        var envelope = new Envelope(message)
        {
            ReplyUri = TransportConstants.RepliesUri,
            ReplyRequested = typeof(T).ToMessageTypeName(),
            ResponseType = typeof(T),
            TenantId = tenantId ?? bus.TenantId,
            DoNotCascadeResponse = true
        };

        bus.TrackEnvelopeCorrelation(envelope, Activity.Current);

        await InvokeInlineAsync(envelope, cancellation);

        if (envelope.Response == null)
        {
            return default!;
        }

        return (T)envelope.Response;
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, string? tenantId = null)
    {
        var envelope = new Envelope(message)
        {
            TenantId = tenantId ?? bus.TenantId
        };

        bus.TrackEnvelopeCorrelation(envelope, Activity.Current);
        return InvokeInlineAsync(envelope, cancellation);
    }

    public async Task<IContinuation> ExecuteAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope;
        _tracker.ExecutionStarted(envelope!);
        _executionStarted(_logger, envelope!.CorrelationId!, _messageTypeName, envelope.Id, null);

        envelope.Attempts++;

        using var timeout = new CancellationTokenSource(_timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellation);

        try
        {
            await Handler.HandleAsync(context, combined.Token);
            if (context.Envelope.ReplyRequested.IsNotEmpty())
            {
                await context.AssertAnyRequiredResponseWasGenerated();
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

            await context.ClearAllAsync();

            Activity.Current?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            return _rules.DetermineExecutionContinuation(e, envelope);
        }
        finally
        {

            _executionFinished(_logger, envelope.CorrelationId, _messageTypeName, envelope.Id, null);
        }
    }

    public async Task<InvokeResult> InvokeAsync(MessageContext context, CancellationToken cancellation)
    {
        if (context.Envelope == null)
        {
            throw new ArgumentOutOfRangeException(nameof(context.Envelope));
        }

        try
        {
            await Handler.HandleAsync(context, cancellation);
            if (context.Envelope.ReplyRequested.IsNotEmpty())
            {
                await context.AssertAnyRequiredResponseWasGenerated();
            }
            
            return InvokeResult.Success;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Invocation failed!");

            var retry = _rules.TryFindInlineContinuation(e, context.Envelope);
            if (retry == null)
            {
                throw;
            }

            return await retry
                .ExecuteInlineAsync(context, context.Runtime, DateTimeOffset.UtcNow, Activity.Current, cancellation)
                .ConfigureAwait(false);
        }
    }

    internal Executor WrapWithMessageTracking(IMessageSuccessTracker tracker)
    {
        return new Executor(_contextPool, _logger, new CircuitBreakerWrappedMessageHandler(Handler, tracker), _tracker,
            _rules, _timeout);
    }

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

        return new Executor(contextPool, runtime, handler, rules, timeoutSpan);
    }
    
    public static IExecutor Build(IWolverineRuntime runtime, ObjectPool<MessageContext> contextPool,
        HandlerGraph handlerGraph, IMessageHandler handler)
    {
        var chain = (handler as MessageHandler)?.Chain;
        var timeoutSpan = chain?.DetermineMessageTimeout(runtime.Options) ?? 5.Seconds();
        var rules = chain?.Failures.CombineRules(handlerGraph.Failures) ?? handlerGraph.Failures;

        return new Executor(contextPool, runtime, handler, rules, timeoutSpan);
    }
}