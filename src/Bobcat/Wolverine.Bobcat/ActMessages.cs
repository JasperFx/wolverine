using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Bobcat;

/// <summary>
/// wolverine#5077. The messages an act's own code sends, captured at the call rather than inferred afterwards. An act
/// that runs code of its own -- <c>WhenTracked(description, bus =&gt; ...)</c>, <c>WhenActing</c> -- is handed a bus that
/// records every message given to <c>SendAsync</c>, <c>PublishAsync</c> (and so <c>ScheduleAsync</c>) or
/// <c>BroadcastToTopicAsync</c> before passing it on. Whatever a handler or an HTTP endpoint the act invoked sends goes
/// through a bus of its own and is never recorded here: it is what the act caused, not what the act sent.
/// </summary>
/// <remarks>
/// <c>EndpointFor(...).SendAsync(...)</c> goes through the endpoint, not the bus, and is not recorded.
/// </remarks>
internal sealed class ActMessages
{
    private readonly ConcurrentQueue<object> _sent = new();

    /// <summary>What the act's own code sent, in order.</summary>
    public IReadOnlyList<object> Sent => _sent.ToArray();

    internal void Add(object message) => _sent.Enqueue(message);

    /// <summary>A bus that records what is sent through it, then sends it through <paramref name="bus" />.</summary>
    public IMessageBus Wrap(IMessageBus bus) => (IMessageBus)wrap(typeof(IMessageBus), bus);

    /// <summary>
    /// A container whose <see cref="IMessageBus" /> and <see cref="IMessageContext" />, asked for directly, record what is
    /// sent through them. Anything else resolves as it always does.
    /// </summary>
    public IServiceProvider Wrap(IServiceProvider services) => new RecordingServices(services, this);

    private object wrap(Type contract, object target)
    {
        var proxy = DispatchProxy.Create(contract, typeof(RecordingBus));
        ((RecordingBus)proxy).Attach(target, this);
        return proxy;
    }

    private sealed class RecordingServices(IServiceProvider inner, ActMessages messages) : IServiceProvider, ISupportRequiredService
    {
        public object? GetService(Type serviceType) => recording(serviceType, inner.GetService(serviceType));

        public object GetRequiredService(Type serviceType)
            => recording(serviceType, inner.GetRequiredService(serviceType))!;

        private object? recording(Type serviceType, object? service)
            => service is not null && (serviceType == typeof(IMessageBus) || serviceType == typeof(IMessageContext))
                ? messages.wrap(serviceType, service)
                : service;
    }
}

/// <summary>The recording half of <see cref="ActMessages" />: a proxy, so every member of the bus passes straight through.</summary>
internal class RecordingBus : DispatchProxy
{
    private object _target = null!;
    private ActMessages _messages = null!;

    internal void Attach(object target, ActMessages messages)
    {
        _target = target;
        _messages = messages;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.Name is nameof(IMessageBus.SendAsync) or nameof(IMessageBus.PublishAsync) or nameof(IMessageBus.BroadcastToTopicAsync))
        {
            var index = Array.FindIndex(targetMethod.GetParameters(), x => x.Name == "message");
            if (index >= 0 && args?[index] is { } message) _messages.Add(message);
        }

        try
        {
            return targetMethod.Invoke(_target, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}
