using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Transports;
using JasperFx.Core;
using Wolverine.Util;

namespace Wolverine.Runtime.Handlers;

/// <summary>
/// An alternative Executor implementation that adds full structured logging to
/// InvokeAsync() / InvokeInlineAsync() calls — the same log messages that
/// transport-received messages already emit. Activated by setting
/// <see cref="InvokeTracingMode.Full"/> on <see cref="WolverineOptions.InvokeTracing"/>.
/// </summary>
internal class TracingExecutor : IExecutor
{
    public const int MessageSucceededEventId = 104;
    public const int MessageFailedEventId = 105;
    public const int ExecutionStartedEventId = 102;
    public const int ExecutionFinishedEventId = 103;

    private readonly ObjectPool<MessageContext> _contextPool;
    private readonly ILogger _logger;
    private readonly IMessageTracker _tracker;
    private readonly FailureRuleCollection _rules;
    private readonly TimeSpan _timeout;
    private readonly string _messageTypeName;

    private readonly Action<ILogger, string, string, Guid, Exception?> _executionStarted;
    private readonly Action<ILogger, string, string, Guid, Exception?> _executionFinished;
    private readonly Action<ILogger, string, Guid, string, Exception?> _messageSucceeded;
    private readonly Action<ILogger, string, Guid, string, Exception> _messageFailed;

    public TracingExecutor(ObjectPool<MessageContext> contextPool, IWolverineRuntime runtime,
        IMessageHandler handler, FailureRuleCollection rules, TimeSpan timeout)
        : this(contextPool, runtime.LoggerFactory.CreateLogger(handler.MessageType), handler,
            runtime.MessageTracking, rules, timeout)
    {
    }

    public TracingExecutor(ObjectPool<MessageContext> contextPool, ILogger logger,
        IMessageHandler handler, IMessageTracker tracker, FailureRuleCollection rules, TimeSpan timeout)
    {
        _contextPool = contextPool;
        Handler = handler;
        _tracker = tracker;
        _rules = rules;
        _timeout = timeout;
        _logger = logger;

        _messageTypeName = handler.MessageType.ToMessageTypeName();

        _messageSucceeded =
            LoggerMessage.Define<string, Guid, string>(handler.SuccessLogLevel, MessageSucceededEventId,
                "Successfully processed message {Name}#{envelope} from {ReplyUri}");

        _messageFailed = LoggerMessage.Define<string, Guid, string>(LogLevel.Error, MessageFailedEventId,
            "Failed to process message {Name}#{envelope} from {ReplyUri}");

        _executionStarted = LoggerMessage.Define<string, string, Guid>(handler.ProcessingLogLevel,
            ExecutionStartedEventId,
            "{CorrelationId}: Started processing {Name}#{Id}");

        _executionFinished = LoggerMessage.Define<string, string, Guid>(handler.ProcessingLogLevel,
            ExecutionFinishedEventId,
            "{CorrelationId}: Finished processing {Name}#{Id}");
    }

    public IMessageHandler Handler { get; }

    public async Task InvokeInlineAsync(Envelope envelope, CancellationToken cancellation)
    {
        using var activity = Handler.TelemetryEnabled ? WolverineTracing.StartExecuting(envelope) : null;

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
            activity?.SetStatus(ActivityStatusCode.Ok);
            _tracker.ExecutionFinished(envelope);
            _messageSucceeded(_logger, _messageTypeName, envelope.Id,
                envelope.Destination?.ToString() ?? "local", null);
        }
        catch (Exception e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            _tracker.ExecutionFinished(envelope, e);
            _messageFailed(_logger, _messageTypeName, envelope.Id,
                envelope.Destination?.ToString() ?? "local", e);
            throw;
        }
        finally
        {
            _contextPool.Return(context);
            _executionFinished(_logger, envelope.CorrelationId!, _messageTypeName, envelope.Id, null);
            activity?.Stop();
        }
    }

    public async Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        var envelope = new Envelope(message)
        {
            ReplyUri = TransportConstants.RepliesUri,
            ReplyRequested = typeof(T).ToMessageTypeName(),
            ResponseType = typeof(T),
            TenantId = options?.TenantId ?? bus.TenantId,
            DoNotCascadeResponse = true
        };

        options?.Override(envelope);

        bus.TrackEnvelopeCorrelation(envelope, Activity.Current);

        await InvokeInlineAsync(envelope, cancellation);

        if (envelope.Response == null)
        {
            return default!;
        }

        return (T)envelope.Response;
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        var envelope = new Envelope(message)
        {
            TenantId = options?.TenantId ?? bus.TenantId
        };

        options?.Override(envelope);

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
            if (context.Envelope!.ReplyRequested.IsNotEmpty())
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
                .ExecutionFinished(envelope, e);

            await context.ClearAllAsync();

            Activity.Current?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            return _rules.DetermineExecutionContinuation(e, envelope);
        }
        finally
        {
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
            await Handler.HandleAsync(context, cancellation);
            if (context.Envelope.ReplyRequested.IsNotEmpty())
            {
                await context.AssertAnyRequiredResponseWasGenerated();
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
        _executionStarted(_logger, envelope.CorrelationId!, _messageTypeName, envelope.Id, null);

        var context = _contextPool.Get();
        context.ReadEnvelope(envelope, InvocationCallback.Instance);
        envelope.Attempts = 1;

        IAsyncEnumerable<T>? stream = null;

        try
        {
            await InvokeAsync(context, cancellation);
            await context.FlushOutgoingMessagesAsync();
            stream = envelope.Response as IAsyncEnumerable<T>;
            activity?.AddEvent(new ActivityEvent(WolverineTracing.StreamingStarted));
            _tracker.ExecutionFinished(envelope);
            _messageSucceeded(_logger, _messageTypeName, envelope.Id,
                envelope.Destination?.ToString() ?? "local", null);
        }
        catch (Exception e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            _tracker.ExecutionFinished(envelope, e);
            _messageFailed(_logger, _messageTypeName, envelope.Id,
                envelope.Destination?.ToString() ?? "local", e);
            _contextPool.Return(context);
            throw;
        }

        if (stream == null)
        {
            _contextPool.Return(context);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _executionFinished(_logger, envelope.CorrelationId!, _messageTypeName, envelope.Id, null);
            yield break;
        }

        try
        {
            await foreach (var item in stream.WithCancellation(cancellation))
            {
                yield return item;
            }

            activity?.AddEvent(new ActivityEvent(WolverineTracing.StreamingCompleted));
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        finally
        {
            _contextPool.Return(context);
            _executionFinished(_logger, envelope.CorrelationId!, _messageTypeName, envelope.Id, null);
        }
    }
}
