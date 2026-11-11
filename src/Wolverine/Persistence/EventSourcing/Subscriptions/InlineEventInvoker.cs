using System.Diagnostics.CodeAnalysis;
using ImTools;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;

namespace Wolverine.Persistence.EventSourcing.Subscriptions;

/// <summary>
///     The store agnostic half of each store's <c>InlineInvoker</c> (<c>ProcessEventsWithWolverineHandlersInStrictOrder()</c>):
///     executes each event in a page inline through its Wolverine handler, in order. GH-5143.
/// </summary>
internal sealed class InlineEventInvoker
{
    private readonly IWolverineRuntime _runtime;
    private ImHashMap<Type, IMessageInvoker> _invokers = ImHashMap<Type, IMessageInvoker>.Empty;

    public InlineEventInvoker(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task InvokeAsync(EventRange page, ISubscriptionController controller, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var sequence = page.SequenceFloor;
        foreach (var @event in page.Events)
        {
            var invoker = invokerForEventType(@event.GetType());

            try
            {
                await invoker.InvokeAsync(@event, (MessageBus)bus, cancellationToken,
                    options: new DeliveryOptions { TenantId = @event.TenantId }).ConfigureAwait(false);
                sequence = @event.Sequence;
            }
            catch (Exception e)
            {
                if (page.Agent.ErrorOptions.SkipApplyErrors)
                {
                    await controller.RecordDeadLetterEventAsync(@event, e).ConfigureAwait(false);
                }
                else
                {
                    // Report an error and stop the subscription!
                    await controller.ReportCriticalFailureAsync(e, sequence).ConfigureAwait(false);
                    return;
                }
            }
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Closes InnerDataInvoker<T> over an event data type that is statically rooted by its handler's registration. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closes InnerDataInvoker<T> over an event data type that is statically rooted by its handler's registration. See AOT guide.")]
    private IMessageInvoker invokerForEventType(Type wrappedType)
    {
        if (_invokers.TryFind(wrappedType, out var invoker))
        {
            return invoker;
        }

        invoker = _runtime.FindInvoker(wrappedType);
        if (invoker is not NoHandlerExecutor)
        {
            _invokers = _invokers.AddOrUpdate(wrappedType, invoker);
            return invoker;
        }

        var innerType = wrappedType.GetGenericArguments()[0];
        var innerHandler = _runtime.FindInvoker(innerType);
        if (innerHandler is not NoHandlerExecutor)
        {
            invoker = typeof(InnerDataInvoker<>).CloseAndBuildAs<IMessageInvoker>(innerHandler, innerType);
            _invokers = _invokers.AddOrUpdate(wrappedType, invoker);
            return invoker;
        }

        invoker = NulloMessageInvoker.Instance;
        _invokers = _invokers.AddOrUpdate(wrappedType, invoker);
        return invoker;
    }
}

/// <summary>
///     Invokes the handler for an event's data when there is no handler for the <c>IEvent&lt;T&gt;</c> wrapper.
/// </summary>
internal sealed class InnerDataInvoker<T> : IMessageInvoker where T : notnull
{
    private readonly IMessageInvoker _inner;

    public InnerDataInvoker(IMessageInvoker inner)
    {
        _inner = inner;
    }

    public Task<T1> InvokeAsync<T1>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null,
        DeliveryOptions? options = null)
    {
        throw new NotSupportedException();
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null,
        DeliveryOptions? options = null)
    {
        if (message is IEvent<T> e)
        {
            return _inner.InvokeAsync(e.Data!, bus, cancellation, timeout, options);
        }

        return Task.CompletedTask;
    }

    public IAsyncEnumerable<T1> StreamAsync<T1>(object message, MessageBus bus,
        CancellationToken cancellation = default, DeliveryOptions? options = null)
        => throw new NotSupportedException();
}

/// <summary>
///     Skips an event that has no handler at all.
/// </summary>
internal sealed class NulloMessageInvoker : IMessageInvoker
{
    public static readonly NulloMessageInvoker Instance = new();

    public Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null,
        DeliveryOptions? options = null)
    {
        throw new NotSupportedException();
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null,
        DeliveryOptions? options = null)
    {
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<T> StreamAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default, DeliveryOptions? options = null)
        => throw new NotSupportedException();
}
