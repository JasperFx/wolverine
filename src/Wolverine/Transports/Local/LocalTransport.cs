using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Util;

namespace Wolverine.Transports.Local;

internal class LocalTransport : TransportBase<LocalQueue>, ILocalMessageRoutingConvention
{
    private readonly Cache<string, LocalQueue> _queues;
    
    private Action<Type, IListenerConfiguration> _customization = (_, _) => { };
    private Func<Type, string> _determineName = t => t.ToMessageTypeName().Replace("+", ".");

    public Dictionary<Type, LocalQueue> Assignments { get; } = new();


    public LocalTransport() : base(TransportConstants.Local, "Local (In Memory)")
    {
        _queues = new(name => new LocalQueue(name));

        _queues.FillDefault(TransportConstants.Default);
        _queues.FillDefault(TransportConstants.Replies);

        _queues[TransportConstants.Durable].Mode = EndpointMode.Durable;
    }

    protected override IEnumerable<LocalQueue> endpoints()
    {
        return _queues;
    }

    protected override LocalQueue findEndpointByUri(Uri uri)
    {
        var queueName = QueueName(uri);
        var settings = _queues[queueName];

        return settings;
    }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        return ValueTask.CompletedTask;
    }

    public override Endpoint ReplyEndpoint()
    {
        return _queues[TransportConstants.Replies];
    }


    public IEnumerable<LocalQueue> AllQueues()
    {
        return _queues;
    }

    /// <summary>
    ///     Retrieves a local queue by name
    /// </summary>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public LocalQueue QueueFor(string queueName)
    {
        return _queues[queueName.ToLowerInvariant()];
    }

    public static string QueueName(Uri uri)
    {
        if (uri == TransportConstants.LocalUri)
        {
            return TransportConstants.Default;
        }

        if (uri == TransportConstants.DurableLocalUri)
        {
            return TransportConstants.Durable;
        }

        if (uri.Scheme == TransportConstants.Local)
        {
            return uri.Host;
        }

        var lastSegment = uri.Segments.Skip(1).LastOrDefault();

        return lastSegment ?? TransportConstants.Default;
    }

    public static Uri AtQueue(Uri uri, string queueName)
    {
        if (queueName.IsEmpty())
        {
            return uri;
        }

        if (uri.Scheme == TransportConstants.Local && uri.Host != TransportConstants.Durable)
        {
            return new Uri("local://" + queueName);
        }

        return new Uri(uri, queueName);
    }
    
    internal void DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        var transport = runtime.Options.Transports.OfType<LocalTransport>().Single();

        foreach (var messageType in handledMessageTypes)
        {
            var queueName = messageType.HasAttribute<LocalQueueAttribute>() 
                ? messageType.GetAttribute<LocalQueueAttribute>()!.QueueName 
                : _determineName(messageType);
            
            if (queueName.IsEmpty()) continue;
            
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
    ///     Override the type to local queue naming. By default this is the MessageTypeName
    ///     to lower case invariant
    /// </summary>
    /// <param name="determineName"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ILocalMessageRoutingConvention Named(Func<Type, string> determineName)
    {
        _determineName = determineName ?? throw new ArgumentNullException(nameof(determineName));
        return this;
    }

    /// <summary>
    ///     Customize the endpoints
    /// </summary>
    /// <param name="customization"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ILocalMessageRoutingConvention CustomizeQueues(Action<Type, IListenerConfiguration> customization)
    {
        _customization = customization ?? throw new ArgumentNullException(nameof(customization));
        return this;
    }
    
}