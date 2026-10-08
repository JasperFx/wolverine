using NATS.Client.JetStream.Models;
using Wolverine.Configuration;
using Wolverine.Nats.Configuration;
using Wolverine.Runtime.Partitioning;
using StreamConfiguration = Wolverine.Nats.Configuration.StreamConfiguration;

namespace Wolverine.Nats.Internal;

public class PartitionedMessageTopologyWithSubjects : PartitionedMessageTopology<NatsListenerConfiguration, NatsSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithSubjects(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        var transport = options.NatsTransport();
        var endpoint = transport.EndpointForSubject(name);

        // GH-3467: the global partitioning topology forces EndpointMode.Durable on every slot, and a
        // NatsEndpoint only supports Durable when it is backed by JetStream -- so without this, every
        // UseShardedNatsSubjects() call threw "Endpoint of type NatsEndpoint does not support
        // EndpointMode.Durable" at configuration time. Stream naming matches
        // NatsListenerConfiguration.UseJetStream()'s default.
        endpoint.UseJetStream = true;
        endpoint.StreamName ??= name.Replace(".", "_").ToUpper();

        transport.Configuration.EnableJetStream = true;

        // ...and declare the backing stream so AutoProvision actually creates it. Without a declared
        // stream the listener failed at startup with NatsJSApiException "stream not found". Work-queue
        // retention matches the topology's competing-consumer, one-node-per-shard semantics, and it keeps
        // a message until it is acknowledged even while no consumer is bound -- at startup, or while the
        // shard moves to another node. Set directly: AsWorkQueue() means interest retention, which drops
        // a message nobody is interested in on arrival. An explicitly declared stream of the same name wins.
        if (!transport.Configuration.Streams.ContainsKey(endpoint.StreamName))
        {
            var stream = new StreamConfiguration
            {
                Name = endpoint.StreamName,
                Retention = StreamConfigRetention.Workqueue
            };
            stream.WithSubjects(name);
            transport.Configuration.Streams[endpoint.StreamName] = stream;
        }

        return endpoint;
    }

    protected override NatsListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToNatsSubject(name);
    }

    protected override NatsSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToNatsSubject(name);
    }
}
