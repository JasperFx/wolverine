using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Newtonsoft.Json;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.Configuration;

public class ListenerConfiguration : ListenerConfiguration<IListenerConfiguration, Endpoint>, IListenerConfiguration
{
    public ListenerConfiguration(Endpoint endpoint) : base(endpoint)
    {
    }
}

public class InteroperableListenerConfiguration<TSelf, TEndpoint, TMapper, TConcreteMapper> : ListenerConfiguration<TSelf, TEndpoint>
    where TSelf : IListenerConfiguration<TSelf> 
    where TEndpoint : Endpoint<TMapper, TConcreteMapper>
    where TConcreteMapper : IEnvelopeMapper, TMapper
{
    public InteroperableListenerConfiguration(TEndpoint endpoint) : base(endpoint)
    {
    }

    public InteroperableListenerConfiguration(Func<TEndpoint> source) : base(source)
    {
    }
    
    /// <summary>
    /// Use a custom interoperability strategy to map Wolverine messages to an upstream
    /// system's protocol
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public TSelf UseInterop(TMapper mapper)
    {
        add(e => e.EnvelopeMapper = mapper);
        return this.As<TSelf>();
    }

    /// <summary>
    /// Customize the basic envelope mapping for interoperability. This mechanism
    /// is suitable if you are mostly needing to modify how headers are communicated
    /// from and to external systems through the underlying transport
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public TSelf UseInterop(Action<TEndpoint, TConcreteMapper> configure)
    {
        add(e => e.customizeMapping((m, _) => configure(e, m)));
        return this.As<TSelf>();
    }

    /// <summary>
    /// Create a completely customized mapper using the WolverineRuntime and the current
    /// Endpoint. This is built lazily at system bootstrapping time
    /// </summary>
    /// <param name="factory"></param>
    /// <returns></returns>
    public TSelf UseInterop(Func<IWolverineRuntime, TEndpoint, TMapper> factory)
    {
        add(e => e.registerMapperFactory(r => factory(r, e)));
        return this.As<TSelf>();
    }
}

public class ListenerConfiguration<TSelf, TEndpoint> : DelayedEndpointConfiguration<TEndpoint>,
    IListenerConfiguration<TSelf>
    where TSelf : IListenerConfiguration<TSelf> where TEndpoint : Endpoint
{
    public ListenerConfiguration(TEndpoint endpoint) : base(endpoint)
    {
        add(e => e.IsListener = true);
    }

    public ListenerConfiguration(Func<TEndpoint> source) : base(source)
    {
        add(e => e.IsListener = true);
    }

    /// <summary>
    /// Creates a policy of sharding the processing of incoming messages by the
    /// specified number of slots. Use this to group messages to prevent concurrent
    /// processing of messages with the same GroupId while allowing parallel work across
    /// GroupIds. The number of "slots" reflects the maximum number of parallel messages
    /// that can be handled concurrently
    /// </summary>
    /// <param name="numberOfSlots"></param>
    /// <returns></returns>
    public TSelf ShardListeningByGroupId(ShardSlots numberOfSlots)
    {
        add(e => e.GroupShardingSlotNumber = numberOfSlots);
        return this.As<TSelf>();
    }

    /// <summary>
    /// In the case of being part of tenancy aware group of message transports, this
    /// setting makes this listening endpoint a "global" endpoint rather than a tenant id
    /// aware endpoint that spans multiple message brokers. 
    /// </summary>
    /// <returns></returns>
    public TSelf GlobalListener()
    {
        add(e => e.TenancyBehavior = TenancyBehavior.Global);
        return this.As<TSelf>();
    }

    /// <summary>
    /// "Pin" this endpoint so that it is only active on the leader node
    /// </summary>
    /// <returns></returns>
    public TSelf ListenOnlyAtLeader()
    {
        add(e =>
        {
            e.ListenerScope = ListenerScope.PinnedToLeader;
            e.IsListener = true;
        });

        return this.As<TSelf>();
    }

    public TSelf ListenWithStrictOrdering(string? endpointName = null)
    {
        if (_endpoint is LocalQueue)
            throw new NotSupportedException(
                $"Wolverine cannot use the {nameof(ListenWithStrictOrdering)} option for local queues. {nameof(Sequential)}() would work for strict ordering *within a single node*, but you will have to use an external message queue for strictly ordering globally across the entire application (assuming your application is clustered)");
        
        add(e =>
        {
            e.IsListener = true;
            e.ListenerScope = ListenerScope.Exclusive;
            e.MaxDegreeOfParallelism = 1;
            e.ListenerCount = 1;

            if (endpointName.IsNotEmpty())
            {
                e.EndpointName = endpointName;
            }
        });

        return this.As<TSelf>();
    }

    /// <summary>
    /// Configure this listener to run exclusively on a single node in the cluster,
    /// but allow parallel message processing within that node. This is useful for
    /// scenarios where you need single-node consistency but want to maintain throughput.
    /// </summary>
    /// <param name="maxParallelism">Maximum number of messages to process in parallel on the exclusive node. Default is 10.</param>
    /// <param name="endpointName">Optional endpoint name for identification</param>
    /// <returns>The configuration for method chaining</returns>
    public TSelf ExclusiveNodeWithParallelism(int maxParallelism = 10, string? endpointName = null)
    {
        if (maxParallelism < 1)
        {
            throw new ArgumentException("Maximum parallelism must be at least 1", nameof(maxParallelism));
        }

        if (maxParallelism > 100)
        {
            // Warning for very high parallelism values that might indicate misunderstanding
            // This is logged but doesn't prevent configuration
            System.Diagnostics.Debug.WriteLine(
                $"WARNING: Setting maxParallelism to {maxParallelism} is very high. " +
                "This will consume significant memory and thread pool resources. " +
                "Consider if this high level of parallelism is necessary for your use case.");
        }

        if (_endpoint is LocalQueue)
            throw new NotSupportedException(
                $"Wolverine cannot use the {nameof(ExclusiveNodeWithParallelism)} option for local queues. Use an external message queue for exclusive node processing across a clustered application.");

        add(e =>
        {
            e.IsListener = true;
            e.ListenerScope = ListenerScope.Exclusive;
            e.MaxDegreeOfParallelism = maxParallelism;
            e.ListenerCount = 1; // Single listener instance for exclusive node

            if (endpointName.IsNotEmpty())
            {
                e.EndpointName = endpointName;
            }
        });

        return this.As<TSelf>();
    }

    /// <summary>
    /// Configure this listener to run exclusively on a single node with parallel processing,
    /// but maintain ordering within specific groups (sessions). This is particularly useful
    /// for Azure Service Bus with sessions or similar scenarios.
    /// </summary>
    /// <param name="maxParallelSessions">Maximum number of sessions/groups to process in parallel. Default is 10.</param>
    /// <param name="endpointName">Optional endpoint name for identification</param>
    /// <returns>The configuration for method chaining</returns>
    public TSelf ExclusiveNodeWithSessionOrdering(int maxParallelSessions = 10, string? endpointName = null)
    {
        if (maxParallelSessions < 1)
        {
            throw new ArgumentException("Maximum parallel sessions must be at least 1", nameof(maxParallelSessions));
        }

        if (maxParallelSessions > 100)
        {
            // Warning for very high session count that might indicate misunderstanding
            // This is logged but doesn't prevent configuration
            System.Diagnostics.Debug.WriteLine(
                $"WARNING: Setting maxParallelSessions to {maxParallelSessions} is very high. " +
                "This will consume significant memory and thread pool resources. " +
                "Consider if this many concurrent sessions is necessary for your use case.");
        }

        if (_endpoint is LocalQueue)
            throw new NotSupportedException(
                $"Wolverine cannot use the {nameof(ExclusiveNodeWithSessionOrdering)} option for local queues. Use an external message queue for exclusive node processing across a clustered application.");

        add(e =>
        {
            e.IsListener = true;
            e.ListenerScope = ListenerScope.Exclusive;
            e.MaxDegreeOfParallelism = maxParallelSessions;
            e.ListenerCount = maxParallelSessions; // Multiple listeners for different sessions

            if (endpointName.IsNotEmpty())
            {
                e.EndpointName = endpointName;
            }
        });

        return this.As<TSelf>();
    }

    public TSelf TelemetryEnabled(bool isEnabled)
    {
        add(e => e.TelemetryEnabled = isEnabled);
        return this.As<TSelf>();
    }

    public TSelf MaximumParallelMessages(int maximumParallelHandlers)
    {
        add(e =>
        {
            e.MaxDegreeOfParallelism = maximumParallelHandlers;
        });
        
        return this.As<TSelf>();
    }

    public TSelf UseDurableInbox(BufferingLimits limits)
    {
        add(e => e.BufferingLimits = limits);
        return UseDurableInbox();
    }

    public TSelf BufferedInMemory(BufferingLimits limits)
    {
        add(e => e.BufferingLimits = limits);
        return BufferedInMemory();
    }

    public TSelf Sequential()
    {
        add(e =>
        {
            e.MaxDegreeOfParallelism = 1;
        });

        return this.As<TSelf>();
    }
    
    public TSelf AddStickyHandler(Type handlerType)
    {
        // This needs to be done eagerly
        _endpoint.StickyHandlers.Add(handlerType);
        return this.As<TSelf>();
    }

    public TSelf UseDurableInbox()
    {
        add(e => e.Mode = EndpointMode.Durable);
        return this.As<TSelf>();
    }

    public TSelf BufferedInMemory()
    {
        add(e => e.Mode = EndpointMode.BufferedInMemory);
        return this.As<TSelf>();
    }

    public TSelf ProcessInline()
    {
        add(e =>
        {
            e.Mode = EndpointMode.Inline;
            e.MaxDegreeOfParallelism = 1;
        });
        return this.As<TSelf>();
    }

    public TSelf UseForReplies()
    {
        add(e => e.IsUsedForReplies = true);
        return this.As<TSelf>();
    }

    public TSelf Named(string name)
    {
        add(e => e.EndpointName = name);
        return this.As<TSelf>();
    }

    public TSelf CustomNewtonsoftJsonSerialization(JsonSerializerSettings customSettings)
    {
        add(e =>
        {
            var serializer = new NewtonsoftSerializer(customSettings);

            e.DefaultSerializer = serializer;
        });

        return this.As<TSelf>();
    }

    public TSelf DefaultSerializer(IMessageSerializer serializer)
    {
        add(e =>
        {
            e.RegisterSerializer(serializer);
            e.DefaultSerializer = serializer;
        });

        return this.As<TSelf>();
    }

    public TSelf MessageBatchSize(int batchSize)
    {
        add(e =>
        {
            e.MessageBatchSize = batchSize;
        });
        return this.As<TSelf>();
    }

    /// <summary>
    ///     To optimize the message listener throughput,
    ///     start up multiple listening endpoints. This is
    ///     most necessary when using inline processing
    /// </summary>
    /// <param name="count"></param>
    /// <returns></returns>
    public TSelf ListenerCount(int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Must be greater than zero");
        }

        add(e => e.ListenerCount = count);

        return this.As<TSelf>();
    }

    /// <summary>
    ///     Assume that any unidentified, incoming message types is the
    ///     type "T". This is primarily for interoperability with non-Wolverine
    ///     applications
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public TSelf DefaultIncomingMessage<T>()
    {
        return DefaultIncomingMessage(typeof(T));
    }

    /// <summary>
    ///     Assume that any unidentified, incoming message types is the
    ///     type "T". This is primarily for interoperability with non-Wolverine
    ///     applications
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public TSelf DefaultIncomingMessage(Type messageType)
    {
        add(e => e.MessageType = messageType);
        return this.As<TSelf>();
    }
}

public enum ProcessingOrder
{
    /// <summary>
    ///     Should the messages be processed in the strict order in which they
    ///     were received?
    /// </summary>
    StrictOrdered,

    /// <summary>
    ///     Is it okay to allow the local queue to process messages in any order? This
    ///     may give better throughput
    /// </summary>
    UnOrdered
}