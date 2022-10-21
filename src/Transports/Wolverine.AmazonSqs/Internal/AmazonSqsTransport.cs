using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Baseline;
using Oakton.Resources;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsTransport : TransportBase<AmazonSqsEndpoint>, IAmazonSqsTransportConfiguration
{
    private Func<IWolverineRuntime, AWSCredentials>? _credentialSource;

    public AmazonSqsTransport() : base("sqs", "Amazon SQS")
    {
        Queues = new(name => new AmazonSqsEndpoint(name, this));
    }

    internal AmazonSqsTransport(IAmazonSQS client) : this()
    {
        Client = client;
    }

    public Cache<string, AmazonSqsEndpoint> Queues { get; }

    public AmazonSQSConfig Config { get; } = new();
    public bool AutoProvision { get; set; }
    public bool AutoPurgeOnStartup { get; set; }

    protected override IEnumerable<AmazonSqsEndpoint> endpoints()
    {
        return Queues;
    }

    protected override AmazonSqsEndpoint findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != Protocol) throw new ArgumentOutOfRangeException(nameof(uri));

        return Queues[uri.Host];
    }

    public override async ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        Client ??= BuildClient(runtime);

        foreach (var endpoint in Queues)
        {
            await endpoint.InitializeAsync();
        }
    }

    public IAmazonSQS BuildClient(IWolverineRuntime runtime)
    {
        if (_credentialSource == null)
        {
            return new AmazonSQSClient(Config);
        }

        var credentials = _credentialSource(runtime);
        return new AmazonSQSClient(credentials, Config);
    }

    internal AmazonSqsEndpoint EndpointForQueue(string queueName)
    {
        return Queues[queueName];
    }

    internal IAmazonSQS? Client { get; private set; }

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

    public override bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource resource)
    {
        resource = new AmazonSqsTransportStatefulResource(this, runtime);
        return true;
    }
}