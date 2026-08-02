using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime.Routing;

namespace Wolverine.Runtime.Handlers;

internal class NoHandlerExecutor : IExecutor
{
    private readonly IContinuation _continuation;
    private readonly Type _messageType;
    private readonly WolverineRuntime _runtime;
    private readonly IMessageTracker? _tracker;

    public NoHandlerExecutor(Type messageType, WolverineRuntime runtime, IMessageTracker? tracker = null)
    {
        _messageType = messageType;
        _runtime = runtime;
        _tracker = tracker;
        var handlers = runtime.MissingHandlers();
        _continuation = new NoHandlerContinuation(handlers, runtime);
    }

    public Exception? Exception { get; set; }

    public Task<IContinuation> ExecuteAsync(MessageContext context, CancellationToken cancellation)
    {
        // Lets NoHandlerContinuation honor a metrics-silent selection for unhandled
        // system messages (GH-907 / #3774)
        context.Tracker = _tracker;
        return Task.FromResult(_continuation);
    }

    // Should never happen
    public Task InvokeInlineAsync(Envelope envelope, CancellationToken cancellation)
    {
        throw new NotSupportedException();
    }

    public Task<InvokeResult> InvokeAsync(MessageContext context, CancellationToken cancellation)
    {
        var handlerAssemblies = _runtime
            .Options
            .HandlerGraph
            .Discovery
            .Assemblies
            .Select(x => x.FullName!)
            .Join(", ");

        throw new NotSupportedException(
            $"No known handler for message type {_messageType.FullNameInCode()}. Wolverine was looking for handlers in assemblies {handlerAssemblies}");
    }

    public Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        if (Exception != null)
        {
            throw Exception;
        }

        throw new IndeterminateRoutesException(_messageType);
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        if (Exception != null)
        {
            throw Exception;
        }

        return Task.CompletedTask;
    }

    public IAsyncEnumerable<T> StreamAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        DeliveryOptions? options = null)
    {
        if (Exception != null)
        {
            throw Exception;
        }

        throw new IndeterminateRoutesException(_messageType);
    }
}