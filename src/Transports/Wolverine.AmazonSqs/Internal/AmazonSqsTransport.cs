using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Baseline;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsTransport : BrokerTransport<AmazonSqsEndpoint>
{
    public AmazonSqsTransport() : base("sqs", "Amazon SQS")
    {
        Queues = new(name => new AmazonSqsEndpoint(name, this));
    }

    internal AmazonSqsTransport(IAmazonSQS client) : this()
    {
        Client = client;
    }

    public Func<IWolverineRuntime, AWSCredentials>? CredentialSource { get; set; }

    public LightweightCache<string, AmazonSqsEndpoint> Queues { get; }

    public AmazonSQSConfig Config { get; } = new();

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
        if (CredentialSource == null)
        {
            return new AmazonSQSClient(Config);
        }

        var credentials = CredentialSource(runtime);
        return new AmazonSQSClient(credentials, Config);
    }

    internal AmazonSqsEndpoint EndpointForQueue(string queueName)
    {
        return Queues[queueName];
    }

    internal IAmazonSQS? Client { get; private set; }
    

    public int LocalStackPort { get; set; }

    public bool UseLocalStackInDevelopment { get; set; }

    public override bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource resource)
    {
        resource = new AmazonSqsTransportStatefulResource(this, runtime);
        return true;
    }

    internal void ConnectToLocalStack(int port = 4566)
    {
        CredentialSource = _ => new BasicAWSCredentials("ignore", "ignore");
        Config.ServiceURL = $"http://localhost:{port}";
    }
}