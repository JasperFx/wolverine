using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
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
 - configure the queues
 - dedicated reply queue?
 
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

    internal AmazonSqsTransport(IAmazonSQS client) : this()
    {
        Client = client;
    }

    public AmazonSQSConfig Config { get; } = new();
    public bool AutoProvision { get; set; }
    public bool AutoPurgeOnStartup { get; set; }

    protected override IEnumerable<AmazonSqsEndpoint> endpoints()
    {
        return _queues;
    }

    protected override AmazonSqsEndpoint findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != Protocol) throw new ArgumentOutOfRangeException(nameof(uri));

        return _queues[uri.Host];
    }

    public override async ValueTask InitializeAsync(IWolverineRuntime runtime)
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

        foreach (var endpoint in _queues)
        {
            await endpoint.InitializeAsync();
        }

    }

    internal AmazonSqsEndpoint EndpointForQueue(string queueName)
    {
        return _queues[queueName];
    }

    internal IAmazonSQS Client { get; private set; }

    IAmazonSqsTransportConfiguration IAmazonSqsTransportConfiguration.Credentials(AWSCredentials credentials)
    {
        _credentialSource = r => credentials;
        return this;
    }

    IAmazonSqsTransportConfiguration IAmazonSqsTransportConfiguration.Credentials(Func<IWolverineRuntime, AWSCredentials> credentialSource)
    {
        _credentialSource = credentialSource;
        return this;
    }

    IAmazonSqsTransportConfiguration IAmazonSqsTransportConfiguration.AutoProvision()
    {
        AutoProvision = true;
        return this;
    }

    IAmazonSqsTransportConfiguration IAmazonSqsTransportConfiguration.AutoPurgeOnStartup()
    {
        AutoPurgeOnStartup = true;
        return this;
    }
}