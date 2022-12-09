using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Wolverine.ErrorHandling;

namespace Wolverine.Runtime.Handlers;

internal class NoHandlerExecutor : IExecutor
{
    private readonly IContinuation _continuation;
    private readonly Type _messageType;
    private readonly WolverineRuntime _runtime;

    public NoHandlerExecutor(Type messageType, WolverineRuntime runtime)
    {
        _messageType = messageType;
        _runtime = runtime;
        var handlers = runtime.MissingHandlers();
        _continuation = new NoHandlerContinuation(handlers, runtime);
    }

    public Task<IContinuation> ExecuteAsync(MessageContext context, CancellationToken cancellation)
    {
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
            .Source
            .Assemblies
            .Select(x => x.FullName)
            .Join(", ");
        
        throw new NotSupportedException($"No known handler for message type {_messageType.FullNameInCode()}. Wolverine was looking for handlers in assemblies {handlerAssemblies}");
    }

    public Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        var handlerAssemblies = _runtime
            .Options
            .HandlerGraph
            .Source
            .Assemblies
            .Select(x => x.FullName)
            .Join(", ");
        
        throw new NotSupportedException($"No known handler for message type {_messageType.FullNameInCode()}. Wolverine was looking for handlers in assemblies {handlerAssemblies}");
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        var handlerAssemblies = _runtime
            .Options
            .HandlerGraph
            .Source
            .Assemblies
            .Select(x => x.FullName)
            .Join(", ");
        
        throw new NotSupportedException($"No known handler for message type {_messageType.FullNameInCode()}. Wolverine was looking for handlers in assemblies {handlerAssemblies}");
    }
}