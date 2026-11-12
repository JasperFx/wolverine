using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Runtime.Handlers;

/// <summary>
/// GH-5138. What <c>IMessageBus.InvokeAsync()</c>, <c>InvokeAsync&lt;T&gt;()</c>, <c>StreamAsync&lt;T&gt;()</c> and the HTTP
/// transport's <c>/invoke</c> run for a message type handled by more than one handler type, and therefore by one
/// handler chain per handler type:
/// <list type="bullet">
/// <item><c>InvokeAsync(message)</c> runs every chain inline, one after the other in discovery order. Each one has
/// its own scope, its own transaction and its own error handling, exactly as it would on its own endpoint. The
/// first failure stops it, and is rethrown as a <see cref="HandlerInvocationException"/> that names the handler
/// type that failed. The chains that ran before it have already committed</item>
/// <item><c>InvokeAsync&lt;T&gt;(message)</c> and <c>StreamAsync&lt;T&gt;(message)</c> run the one chain whose handler
/// returns a <c>T</c> (an <c>IAsyncEnumerable&lt;T&gt;</c> for a stream), and only that chain. With no such chain, or
/// more than one, they throw an <see cref="AmbiguousHandlerChainException"/></item>
/// </list>
/// A message type with a single chain never gets here, and keeps the plain executor of that chain.
/// </summary>
internal class MultipleChainInvoker : IExecutor
{
    private readonly MessageHandlerGroup _group;
    private readonly Func<HandlerChain, IExecutor> _executorFor;
    private readonly Lazy<(HandlerChain Chain, IExecutor Executor)[]> _executors;

    // One resolution per response type, made the first time it is asked for
    private ImHashMap<Type, IExecutor> _byResponseType = ImHashMap<Type, IExecutor>.Empty;

    public MultipleChainInvoker(MessageHandlerGroup group, Func<HandlerChain, IExecutor> executorFor)
    {
        _group = group;
        _executorFor = executorFor;

        // Built lazily, so a chain is compiled when it is first invoked rather than when its invoker is resolved
        _executors = new Lazy<(HandlerChain, IExecutor)[]>(() =>
            _group.Chains.Select(chain => (chain, _executorFor(chain))).ToArray());
    }

    public Type MessageType => _group.MessageType;

    public async Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        foreach (var (chain, executor) in _executors.Value)
        {
            try
            {
                await executor.InvokeAsync(message, bus, cancellation, timeout, options).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new HandlerInvocationException(_group, chain, e);
            }
        }
    }

    public Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        return executorForResponse(typeof(T), typeof(T).FullNameInCode())
            .InvokeAsync<T>(message, bus, cancellation, timeout, options);
    }

    public IAsyncEnumerable<T> StreamAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        DeliveryOptions? options = null)
    {
        return executorForResponse(typeof(IAsyncEnumerable<T>), $"IAsyncEnumerable<{typeof(T).FullNameInCode()}>")
            .StreamAsync<T>(message, bus, cancellation, options);
    }

    /// <summary>
    /// The HTTP transport's inline invocation. A request that asks for a reply runs the one chain that produces it,
    /// as <c>InvokeAsync&lt;T&gt;</c> does; anything else runs every chain, as <c>InvokeAsync</c> does, each on its own
    /// copy of the envelope
    /// </summary>
    public async Task InvokeInlineAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (envelope.ResponseType != null)
        {
            await executorForResponse(envelope.ResponseType, envelope.ResponseType.FullNameInCode())
                .InvokeInlineAsync(envelope, cancellation).ConfigureAwait(false);
            return;
        }

        foreach (var (chain, executor) in _executors.Value)
        {
            var copy = envelope.CloneForInlineInvocation();
            try
            {
                await executor.InvokeInlineAsync(copy, cancellation).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new HandlerInvocationException(_group, chain, e);
            }
        }
    }

    // A received envelope of a message type with several chains is always run by the chain bound to the endpoint
    // it arrived at (see HandlerGraph.HandlerFor(Type, Endpoint)). The endpoint-less pipeline has no such chain
    public Task<IContinuation> ExecuteAsync(MessageContext context, CancellationToken cancellation)
    {
        throw new NoHandlerForEndpointException(_group.MessageType, _group);
    }

    public Task<InvokeResult> InvokeAsync(MessageContext context, CancellationToken cancellation)
    {
        throw new NoHandlerForEndpointException(_group.MessageType, _group);
    }

    private IExecutor executorForResponse(Type responseType, string description)
    {
        if (_byResponseType.TryFind(responseType, out var executor)) return executor;

        var matches = _executors.Value
            .Where(x => ResponseTypesOf(x.Chain).Any(t => t.CanBeCastTo(responseType)))
            .ToArray();

        if (matches.Length != 1)
        {
            throw new AmbiguousHandlerChainException(_group.MessageType,
                describeResponseAmbiguity(description, matches.Select(x => x.Chain).ToArray()));
        }

        executor = matches[0].Executor;
        _byResponseType = _byResponseType.AddOrUpdate(responseType, executor);
        return executor;
    }

    private string describeResponseAmbiguity(string responseType, IReadOnlyList<HandlerChain> matches)
    {
        var messageType = _group.MessageType.FullNameInCode();
        var prefix = $"Message type {messageType} is handled by {_group.Chains.Count} handler types " +
                     $"({_group.DescribeHandlerTypes()}), each with its own handler chain. ";

        return matches.Count == 0
            ? prefix + $"A request for a {responseType} response runs the one chain whose handler returns it, and none of " +
              "them declares that return type. Invoke the message without asking for a response to run every handler, " +
              "or return the response from exactly one handler type."
            : prefix + $"A request for a {responseType} response runs the one chain whose handler returns it, but " +
              $"{MessageHandlerGroup.Describe(matches)} all do. Return the response from exactly one handler type, or ask " +
              "for a response type only one of them returns.";
    }

    /// <summary>
    /// Every type the handler methods of a chain return, which is what a chain can answer a request with. A saga
    /// chain sorts its handler calls into starting, existing and not-found calls when it generates code, so those
    /// are read as well
    /// </summary>
    internal static IEnumerable<Type> ResponseTypesOf(HandlerChain chain)
    {
        var calls = chain is SagaChain saga ? saga.SagaHandlerCalls() : chain.HandlerCalls();
        return calls.SelectMany(x => x.Creates).Select(x => x.VariableType);
    }
}

/// <summary>
/// GH-5138. Thrown by <c>IMessageBus.InvokeAsync()</c> when one of the handler types of a message type that has
/// several fails. Every handler type runs in its own handler chain, with its own transaction, so the handler types
/// that ran before this one have already committed and the ones after it did not run
/// </summary>
public class HandlerInvocationException : Exception
{
    internal HandlerInvocationException(MessageHandlerGroup group, HandlerChain chain, Exception inner) : base(
        buildMessage(group, chain, inner), inner)
    {
        MessageType = group.MessageType;
        HandlerType = MessageHandlerGroup.HandlerTypeOf(chain);
    }

    /// <summary>
    /// The message type that was invoked
    /// </summary>
    public Type MessageType { get; }

    /// <summary>
    /// The handler type whose handler chain failed
    /// </summary>
    public Type HandlerType { get; }

    private static string buildMessage(MessageHandlerGroup group, HandlerChain failed, Exception inner)
    {
        var handlerTypes = group.Chains.Select(MessageHandlerGroup.HandlerTypeOf).ToList();
        var index = handlerTypes.IndexOf(MessageHandlerGroup.HandlerTypeOf(failed));

        var completed = handlerTypes.Take(index).Select(x => x.FullNameInCode()).ToArray();
        var skipped = handlerTypes.Skip(index + 1).Select(x => x.FullNameInCode()).ToArray();

        return $"Handler type {handlerTypes[index].FullNameInCode()} failed while invoking message type " +
               $"{group.MessageType.FullNameInCode()} ({inner.GetType().Name}: {inner.Message}). Each handler type runs " +
               "in its own handler chain and transaction. " +
               (completed.Length == 0 ? "No handler type had run before it. " : $"Already completed: {completed.Join(", ")}. ") +
               (skipped.Length == 0 ? "" : $"Not run: {skipped.Join(", ")}.");
    }
}
