using System.Threading.Tasks.Dataflow;
using JasperFx.Core.Reflection;
using Newtonsoft.Json;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Configuration;

public class ListenerConfiguration : ListenerConfiguration<IListenerConfiguration, Endpoint>, IListenerConfiguration
{
    public ListenerConfiguration(Endpoint endpoint) : base(endpoint)
    {
    }
}

public class ListenerConfiguration<TSelf, TEndpoint> : DelayedEndpointConfiguration<TEndpoint>,
    IListenerConfiguration<TSelf>
    where TSelf : IListenerConfiguration<TSelf> where TEndpoint : Endpoint
{
    public ListenerConfiguration(TEndpoint queue) : base(queue)
    {
        add(e => e.IsListener = true);
    }

    public TSelf MaximumParallelMessages(int maximumParallelHandlers)
    {
        add(e => e.ExecutionOptions.MaxDegreeOfParallelism = maximumParallelHandlers);
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
            e.ExecutionOptions.MaxDegreeOfParallelism = 1;
            e.ExecutionOptions.EnsureOrdered = true;
        });

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
        add(e => e.Mode = EndpointMode.Inline);
        return this.As<TSelf>();
    }

    public TSelf ConfigureExecution(Action<ExecutionDataflowBlockOptions> configure)
    {
        add(e => configure(e.ExecutionOptions));
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
}