using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events.Daemon;
using Marten.Events.Daemon.Internals;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;

namespace Wolverine.Marten.Subscriptions;

internal class InlineInvoker : BatchSubscription
{
    private readonly IWolverineRuntime _runtime;
    private ImHashMap<Type, IMessageInvoker> _invokers = ImHashMap<Type, IMessageInvoker>.Empty;

    public InlineInvoker(string subscriptionName, IWolverineRuntime runtime) : base(subscriptionName)
    {
        _runtime = runtime;
    }

    public override async Task ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentOperations operations, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var sequence = page.SequenceFloor;
        foreach (var @event in page.Events)
        {
            var invoker = invokerForEventType(@event.GetType());

            try
            {
                await invoker.InvokeAsync(@event, (MessageBus)bus, cancellationToken, tenantId: @event.TenantId);
                sequence = @event.Sequence;
            }
            catch (Exception e)
            {
                if (page.Agent.ErrorOptions.SkipApplyErrors)
                {
                    await controller.RecordDeadLetterEventAsync(@event, e);
                }
                else
                {
                    // Report an error and stop the subscription!
                    await controller.ReportCriticalFailureAsync(e, sequence);
                    return;
                }
            }
        }
    }

    private IMessageInvoker invokerForEventType(Type wrappedType)
    {
        if (_invokers.TryFind(wrappedType, out var invoker)) return invoker;

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

        invoker = new NulloMessageInvoker();
        _invokers = _invokers.AddOrUpdate(wrappedType, invoker);
        return invoker;
    }
}