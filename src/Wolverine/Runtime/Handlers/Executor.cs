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

internal enum InvokeResult
{
    Success,
    TryAgain
}

internal interface IExecutor : IMessageInvoker
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
    private readonly IMessageHandler _handler;
    private readonly IMessageTracker _tracker;
    private readonly FailureRuleCollection _rules;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    
    private readonly Action<ILogger, string, Guid, string, Exception> _messageFailed;
    private readonly Action<ILogger, string, Guid, string, Exception?> _messageSucceeded;
    private readonly Action<ILogger, string, string, Guid, Exception?> _executionFinished;
    private readonly Action<ILogger, string, string, Guid, Exception?> _executionStarted;
    
    private readonly string _messageTypeName;

    public Executor(ObjectPool<MessageContext> contextPool, IWolverineRuntime runtime, IMessageHandler handler, FailureRuleCollection rules, TimeSpan timeout)
    {
        _handler = handler;
        _timeout = timeout;
        _rules = rules;
        _tracker = runtime.MessageTracking;
        _contextPool = contextPool;

        _logger = runtime.LoggerFactory.CreateLogger(handler.MessageType);
        
        _messageSucceeded =
            LoggerMessage.Define<string, Guid, string>(handler.ExecutionLogLevel, MessageSucceededEventId,
                "Successfully processed message {Name}#{envelope} from {ReplyUri}");

        _messageFailed = LoggerMessage.Define<string, Guid, string>(LogLevel.Error, MessageFailedEventId,
            "Failed to process message {Name}#{envelope} from {ReplyUri}");

        _executionStarted = LoggerMessage.Define<string, string, Guid>(LogLevel.Debug, ExecutionStartedEventId,
            "{CorrelationId}: Started processing {Name}#{Id}");

        _executionFinished = LoggerMessage.Define<string, string, Guid>(LogLevel.Debug, ExecutionFinishedEventId,
            "{CorrelationId}: Finished processing {Name}#{Id}");
    }

    public Executor(ObjectPool<MessageContext> contextPool, ILogger logger, IMessageHandler handler, IMessageTracker tracker, FailureRuleCollection rules, TimeSpan timeout)
    {
        _contextPool = contextPool;
        _handler = handler;
        _tracker = tracker;
        _rules = rules;
        _timeout = timeout;
        
        _logger = logger;
        
        _messageSucceeded =
            LoggerMessage.Define<string, Guid, string>(LogLevel.Information, MessageSucceededEventId,
                "Successfully processed message {Name}#{envelope} from {ReplyUri}");

        _messageFailed = LoggerMessage.Define<string, Guid, string>(LogLevel.Error, MessageFailedEventId,
            "Failed to process message {Name}#{envelope} from {ReplyUri}");

        _messageTypeName = handler.MessageType.ToMessageTypeName();
        
        _executionStarted = LoggerMessage.Define<string, string, Guid>(LogLevel.Debug, ExecutionStartedEventId,
            "{CorrelationId}: Started processing {Name}#{Id}");

        _executionFinished = LoggerMessage.Define<string, string, Guid>(LogLevel.Debug, ExecutionFinishedEventId,
            "{CorrelationId}: Finished processing {Name}#{Id}");

    }

    public async Task InvokeInlineAsync(Envelope envelope, CancellationToken cancellation)
    {
        using var activity = WolverineTracing.StartExecuting(envelope);

        _tracker.ExecutionStarted(envelope);
        _executionStarted(_logger, envelope.CorrelationId!, _messageTypeName, envelope.Id, null);

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
            Activity.Current?.SetStatus(ActivityStatusCode.Ok);
            _tracker.ExecutionFinished(envelope);
            _executionFinished(_logger, envelope!.CorrelationId, _messageTypeName, envelope.Id, null);
        }
        catch (Exception e)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
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

    public async Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        var envelope = new Envelope(message)
        {
            ReplyUri = TransportConstants.RepliesUri,
            ReplyRequested = typeof(T).ToMessageTypeName(),
            ResponseType = typeof(T)
        };
        
        bus.TrackEnvelopeCorrelation(envelope, Activity.Current);
        
        await InvokeInlineAsync(envelope, cancellation);

        if (envelope.Response == null)
        {
            return default;
        }

        return (T)envelope.Response;
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        var envelope = new Envelope(message);
        bus.TrackEnvelopeCorrelation(envelope, Activity.Current);
        return InvokeInlineAsync(envelope, cancellation);
    }

    public async Task<IContinuation> ExecuteAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope;
        _tracker.ExecutionStarted(envelope!);
        _executionStarted(_logger, envelope.CorrelationId!, _messageTypeName, envelope.Id, null);
        
        envelope!.Attempts++;

        using var timeout = new CancellationTokenSource(_timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellation);

        try
        {
            await _handler.HandleAsync(context, combined.Token);
            Activity.Current?.SetStatus(ActivityStatusCode.Ok);

            _messageSucceeded(_logger, _messageTypeName, envelope.Id,
                envelope.Destination!.ToString(), null);
            
            return MessageSucceededContinuation.Instance;
        }
        catch (Exception e)
        {
            _messageFailed(_logger, _messageTypeName, envelope.Id, envelope.Destination!.ToString(), e);
            
            _logger.LogError(e, "Failure during message processing execution of message {Id}, {Message}", context.Envelope.Id, context.Envelope.Message);
            _tracker
                .ExecutionFinished(envelope, e); // Need to do this to make the MessageHistory complete

            await context.ClearAllAsync();

            Activity.Current?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            return _rules.DetermineExecutionContinuation(e, envelope);
        }
        finally
        {
            _tracker.ExecutionFinished(envelope!);
            _executionFinished(_logger, envelope.CorrelationId!, _messageTypeName, envelope.Id, null);
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
            await _handler.HandleAsync(context, cancellation);
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

            if (retry.Delay.HasValue)
            {
                await Task.Delay(retry.Delay.Value, cancellation).ConfigureAwait(false);
            }

            return InvokeResult.TryAgain;
        }
    }

    internal Executor WrapWithMessageTracking(IMessageSuccessTracker tracker)
    {
        return new Executor(_contextPool, _logger, new CircuitBreakerWrappedMessageHandler(_handler, tracker), _tracker, _rules, _timeout);
    }

    public static IExecutor Build(IWolverineRuntime runtime, ObjectPool<MessageContext> contextPool, HandlerGraph handlerGraph, Type messageType)
    {
        var handler = handlerGraph.HandlerFor(messageType);
        if (handler == null)
        {
            return new NoHandlerExecutor(messageType, (WolverineRuntime)runtime);
        }

        var chain = (handler as MessageHandler)?.Chain;
        var timeoutSpan = chain?.DetermineMessageTimeout(runtime.Options) ?? 5.Seconds();
        var rules = chain?.Failures.CombineRules(handlerGraph.Failures) ?? handlerGraph.Failures;

        return new Executor(contextPool, runtime, handler, rules, timeoutSpan);
    }
}