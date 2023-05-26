using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
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
    private readonly ObjectPool<MessageContext> _contextPool;
    private readonly IMessageHandler _handler;
    private readonly IMessageLogger _logger;
    private readonly FailureRuleCollection _rules;
    private readonly TimeSpan _timeout;

    public Executor(ObjectPool<MessageContext> contextPool, IWolverineRuntime runtime, IMessageHandler handler, FailureRuleCollection rules, TimeSpan timeout)
    {
        _handler = handler;
        _timeout = timeout;
        _rules = rules;
        _logger = runtime.MessageLogger;
        _contextPool = contextPool;
    }

    public Executor(ObjectPool<MessageContext> contextPool, IMessageHandler handler, IMessageLogger logger, FailureRuleCollection rules, TimeSpan timeout)
    {
        _contextPool = contextPool;
        _handler = handler;
        _logger = logger;
        _rules = rules;
        _timeout = timeout;
    }

    public async Task InvokeInlineAsync(Envelope envelope, CancellationToken cancellation)
    {
        using var activity = WolverineTracing.StartExecuting(envelope);

        _logger.ExecutionStarted(envelope);

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
            _logger.ExecutionFinished(envelope);
        }
        catch (Exception e)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            _logger.ExecutionFinished(envelope, e);
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
        context.Envelope!.Attempts++;

        using var timeout = new CancellationTokenSource(_timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellation);

        try
        {
            await _handler.HandleAsync(context, combined.Token);
            Activity.Current?.SetStatus(ActivityStatusCode.Ok);
            return MessageSucceededContinuation.Instance;
        }
        catch (Exception e)
        {
            _logger.LogException(e, context.Envelope!.Id, "Failure during message processing execution");
            _logger
                .ExecutionFinished(context.Envelope, e); // Need to do this to make the MessageHistory complete

            await context.ClearAllAsync();

            Activity.Current?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            return _rules.DetermineExecutionContinuation(e, context.Envelope);
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
            _logger.LogException(e, message: $"Invocation of {context.Envelope} failed!");

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
        return new Executor(_contextPool, new CircuitBreakerWrappedMessageHandler(_handler, tracker), _logger, _rules, _timeout);
    }

    public static IExecutor Build(IWolverineRuntime runtime, ObjectPool<MessageContext> contextPool, HandlerGraph handlerGraph, Type messageType)
    {
        var handler = handlerGraph.HandlerFor(messageType);
        if (handler == null)
        {
            return new NoHandlerExecutor(messageType, (WolverineRuntime)runtime);
        }

        var timeoutSpan = handler.Chain?.DetermineMessageTimeout(runtime.Options) ?? 5.Seconds();
        var rules = handler.Chain?.Failures.CombineRules(handlerGraph.Failures) ?? handlerGraph.Failures;
        return new Executor(contextPool, runtime, handler, rules, timeoutSpan);
    }
}