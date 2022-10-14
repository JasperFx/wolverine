using Amazon.SQS;
using Baseline;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsTransport : TransportBase<AmazonSqsEndpoint>
{
    /* TODO
 - config the batch size for sending and receiving
 - config the polling time
 - does it support delayed delivery? That's cool.
 - default credentials?
 - configure the credentials. Chain in FI?
 - alter the AmazonSQSConfig. Like Rabbit MQ?
 - AutoProvision
 - AutoPurge
 - IStatefulResource support
 - prefix!!!
 - SNS topics
 - conventional routing
 - conventions for SQS endpoints
 
 - On Monday, just make the barebones basics work.
 - UseSqs(Action<AmazonSQSConfig>).Credentials()
 - UseSqsFromLocalstackIfDevelopment() -- be nice to have a DEV setting
 - build client in transport
 - extension methods for listening & subscribing
 - TransportCompliance for durable
 - TransportCompliance for inline
 - TransportCompliance for buffered
 
 */
    
    private readonly Cache<string, AmazonSqsEndpoint> _queues;

    public AmazonSqsTransport() : base("sqs", "Amazon SQS")
    {
        _queues = new(name => new AmazonSqsEndpoint(name, this));
    }

    protected override IEnumerable<AmazonSqsEndpoint> endpoints()
    {
        return _queues;
    }

    protected override AmazonSqsEndpoint findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != Protocol) throw new ArgumentOutOfRangeException(nameof(uri));

        return _queues[uri.Host];
    }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        // TODO -- auto provision and auto purge
        // TODO -- can spin up a dedicated queue for a node?
        return ValueTask.CompletedTask;
        
    }

    internal IAmazonSQS CreateClient()
    {
        throw new NotImplementedException();
    }
}