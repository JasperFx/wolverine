using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Baseline;
using Oakton.Resources;
using Spectre.Console;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSqsTransport : BrokerTransport<AmazonSqsQueue>
{
    public AmazonSqsTransport() : base("sqs", "Amazon SQS")
    {
        Queues = new LightweightCache<string, AmazonSqsQueue>(name => new AmazonSqsQueue(name, this));
        IdentifierDelimiter = "-";
    }

    internal AmazonSqsTransport(IAmazonSQS client) : this()
    {
        Client = client;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('.', '-');
    }

    public Func<IWolverineRuntime, AWSCredentials>? CredentialSource { get; set; }

    public LightweightCache<string, AmazonSqsQueue> Queues { get; }

    public AmazonSQSConfig Config { get; } = new();

    internal IAmazonSQS? Client { get; private set; }


    public int LocalStackPort { get; set; }

    public bool UseLocalStackInDevelopment { get; set; }

    protected override IEnumerable<AmazonSqsQueue> endpoints()
    {
        return Queues;
    }

    protected override AmazonSqsQueue findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != Protocol)
        {
            throw new ArgumentOutOfRangeException(nameof(uri));
        }

        return Queues[uri.Host];
    }

    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        Client ??= BuildClient(runtime);
        return ValueTask.CompletedTask;
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Queue Name", "name");
        yield return new PropertyColumn("Messages",nameof(GetQueueAttributesResponse.ApproximateNumberOfMessages), Justify.Right);
        yield return new PropertyColumn("Delayed", nameof(GetQueueAttributesResponse.ApproximateNumberOfMessagesDelayed), Justify.Right);
        yield return new PropertyColumn("Not Visible", nameof(GetQueueAttributesResponse.ApproximateNumberOfMessagesNotVisible), Justify.Right);
        
        
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

    internal void ConnectToLocalStack(int port = 4566)
    {
        CredentialSource = _ => new BasicAWSCredentials("ignore", "ignore");
        Config.ServiceURL = $"http://localhost:{port}";
    }
}