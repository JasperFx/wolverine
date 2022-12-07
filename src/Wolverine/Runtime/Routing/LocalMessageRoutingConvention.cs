using System;
using System.Collections.Generic;
using System.Linq;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Transports.Local;
using Wolverine.Util;

namespace Wolverine.Runtime.Routing;

public class LocalMessageRoutingConvention 
{
    private Action<Type, IListenerConfiguration> _customization = (_, _) => { };
    private Func<Type, string> _determineName = t => t.ToMessageTypeName().Replace("+", ".");

    /// <summary>
    ///     Optionally include (allow list) or exclude (deny list) types. By default, this will apply to all message types
    /// </summary>
    internal CompositeFilter<Type> TypeFilters { get; } = new();

    public Dictionary<Type, LocalQueueSettings> Assignments { get; } = new();

    internal void DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        var matching = handledMessageTypes.Where(x => TypeFilters.Matches(x));

        var transport = runtime.Options.Transports.OfType<LocalTransport>().Single();

        foreach (var messageType in matching)
        {
            var queueName = messageType.HasAttribute<LocalQueueAttribute>() 
                ? messageType.GetAttribute<LocalQueueAttribute>()!.QueueName 
                : _determineName(messageType);
            
            var queue = transport.AllQueues().FirstOrDefault(x => x.EndpointName == queueName);

            if (queue == null)
            {
                queue = transport.QueueFor(queueName);

                if (_customization != null)
                {
                    var listener = new ListenerConfiguration(queue);
                    _customization(messageType, listener);

                    listener.As<IDelayedEndpointConfiguration>().Apply();
                }
            }

            queue.HandledMessageTypes.Add(messageType);

            Assignments[messageType] = queue;

        }
    }

    internal IEnumerable<Endpoint> DiscoverSenders(Type messageType, IWolverineRuntime runtime)
    {
        if (Assignments.TryGetValue(messageType, out var queue))
        {
            yield return queue;
        }
    }

    /// <summary>
    ///     Create an allow list of included message types. This is accumulative.
    /// </summary>
    /// <param name="filter"></param>
    public LocalMessageRoutingConvention IncludeTypes(Func<Type, bool> filter)
    {
        if (filter == null)
        {
            throw new ArgumentNullException(nameof(filter));
        }

        TypeFilters.Includes.Add(filter);
        return this;
    }

    /// <summary>
    ///     Override the type to local queue naming. By default this is the MessageTypeName
    ///     to lower case invariant
    /// </summary>
    /// <param name="determineName"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public LocalMessageRoutingConvention Named(Func<Type, string> determineName)
    {
        _determineName = determineName ?? throw new ArgumentNullException(nameof(determineName));
        return this;
    }

    /// <summary>
    ///     Create an deny list of included message types. This is accumulative.
    /// </summary>
    /// <param name="filter"></param>
    public LocalMessageRoutingConvention ExcludeTypes(Func<Type, bool> filter)
    {
        if (filter == null)
        {
            throw new ArgumentNullException(nameof(filter));
        }

        TypeFilters.Excludes.Add(filter);
        return this;
    }

    /// <summary>
    ///     Customize the endpoints
    /// </summary>
    /// <param name="customization"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public LocalMessageRoutingConvention CustomizeQueues(Action<Type, IListenerConfiguration> customization)
    {
        _customization = customization ?? throw new ArgumentNullException(nameof(customization));
        return this;
    }
}