using Amazon.Runtime;
using Amazon.SQS;
using Baseline;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsTransport : TransportBase<AmazonSqsEndpoint>, IAmazonSqsTransportConfiguration
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
    private Func<IWolverineRuntime, AWSCredentials>? _credentialSource;

    public AmazonSqsTransport() : base("sqs", "Amazon SQS")
    {
        _queues = new(name => new AmazonSqsEndpoint(name, this));
    }

    public AmazonSQSConfig Config { get; } = new();

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
        if (_credentialSource == null)
        {
            Client = new AmazonSQSClient(Config);
        }
        else
        {
            var credentials = _credentialSource(runtime);
            Client = new AmazonSQSClient(credentials, Config);
        }
        
        // TO
        //
        // DO -- auto provision and auto purge
        // TODO -- can spin up a dedicated queue for a node?
        return ValueTask.CompletedTask;
        
    }

    internal AmazonSQSClient Client { get; private set; }

    public IAmazonSqsTransportConfiguration Credentials(AWSCredentials credentials)
    {
        _credentialSource = r => credentials;
        return this;
    }

    public IAmazonSqsTransportConfiguration Credentials(Func<IWolverineRuntime, AWSCredentials> credentialSource)
    {
        _credentialSource = credentialSource;
        return this;
    }
}