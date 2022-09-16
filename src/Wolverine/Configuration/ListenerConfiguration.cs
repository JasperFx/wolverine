using System;
using System.Threading.Tasks.Dataflow;
using LamarCodeGeneration.Util;
using Newtonsoft.Json;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Configuration;

public class ListenerConfiguration : ListenerConfiguration<IListenerConfiguration, Endpoint>, IListenerConfiguration
{
    public ListenerConfiguration(Endpoint endpoint) : base(endpoint)
    {
    }
}

public class ListenerConfiguration<TSelf, TEndpoint> : IListenerConfiguration<TSelf>
    where TSelf : IListenerConfiguration<TSelf> where TEndpoint : Endpoint
{
    public ListenerConfiguration(TEndpoint endpoint)
    {
        this.endpoint = endpoint;
        endpoint.IsListener = true;
    }

    // ReSharper disable once InconsistentNaming
    protected TEndpoint endpoint { get; }

    public TSelf MaximumParallelMessages(int maximumParallelHandlers)
    {
        endpoint.ExecutionOptions.MaxDegreeOfParallelism = maximumParallelHandlers;
        return this.As<TSelf>();
    }

    public TSelf Sequential()
    {
        endpoint.ExecutionOptions.MaxDegreeOfParallelism = 1;
        endpoint.ExecutionOptions.EnsureOrdered = true;
        return this.As<TSelf>();
    }

    public TSelf UseDurableInbox()
    {
        endpoint.Mode = EndpointMode.Durable;
        return this.As<TSelf>();
    }

    public TSelf UsePersistentInbox()
    {
        return UseDurableInbox();
    }

    public TSelf BufferedInMemory()
    {
        endpoint.Mode = EndpointMode.BufferedInMemory;
        return this.As<TSelf>();
    }

    public TSelf ProcessInline()
    {
        endpoint.Mode = EndpointMode.Inline;
        return this.As<TSelf>();
    }

    public TSelf ConfigureExecution(Action<ExecutionDataflowBlockOptions> configure)
    {
        configure(endpoint.ExecutionOptions);
        return this.As<TSelf>();
    }

    public TSelf UseForReplies()
    {
        endpoint.IsUsedForReplies = true;
        return this.As<TSelf>();
    }

    public TSelf Named(string name)
    {
        endpoint.Name = name;
        return this.As<TSelf>();
    }

    public TSelf CustomNewtonsoftJsonSerialization(JsonSerializerSettings customSettings)
    {
        var serializer = new NewtonsoftSerializer(customSettings);
        endpoint.RegisterSerializer(serializer);

        return this.As<TSelf>();
    }

    public TSelf DefaultSerializer(IMessageSerializer serializer)
    {
        endpoint.RegisterSerializer(serializer);
        endpoint.DefaultSerializer = serializer;
        return this.As<TSelf>();
    }
}
