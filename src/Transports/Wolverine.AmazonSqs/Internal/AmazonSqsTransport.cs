using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Baseline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oakton.Resources;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsTransport : BrokerTransport<AmazonSqsQueue>
{
    public AmazonSqsTransport() : base("sqs", "Amazon SQS")
    {
        Queues = new(name => new AmazonSqsQueue(name, this));
    }

    internal AmazonSqsTransport(IAmazonSQS client) : this()
    {
        Client = client;
    }

    public Func<IWolverineRuntime, AWSCredentials>? CredentialSource { get; set; }

    public LightweightCache<string, AmazonSqsQueue> Queues { get; }

    public AmazonSQSConfig Config { get; } = new();

    protected override IEnumerable<AmazonSqsQueue> endpoints()
    {
        return Queues;
    }

    protected override AmazonSqsQueue findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != Protocol) throw new ArgumentOutOfRangeException(nameof(uri));

        return Queues[uri.Host];
    }

    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        Client ??= BuildClient(runtime);
        return ValueTask.CompletedTask;
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

    internal AmazonSqsQueue EndpointForQueue(string queueName)
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