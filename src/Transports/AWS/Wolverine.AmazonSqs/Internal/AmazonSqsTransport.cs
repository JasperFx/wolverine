using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSqsTransport : BrokerTransport<AmazonEndpoint>
{
    public const string SqsProtocol = "sqs";
    public const string QueueSegment = "queue";
    public const string TopicSegment = "topic";
    
    public const string DeadLetterQueueName = "wolverine-dead-letter-queue";

    public const char Separator = '-';

    public AmazonSqsTransport() : base(SqsProtocol, "Amazon SQS")
    {
        Queues = new LightweightCache<string, AmazonSqsQueue>(name => new AmazonSqsQueue(name, this));
        Topics = new LightweightCache<string, AmazonSnsTopic>(name => new AmazonSnsTopic(name, this));
        IdentifierDelimiter = "-";
    }

    internal AmazonSqsTransport(IAmazonSQS sqsClient) : this()
    {
        SqsClient = sqsClient;
    }

    public Func<IWolverineRuntime, AWSCredentials>? CredentialSource { get; set; }

    public LightweightCache<string, AmazonSqsQueue> Queues { get; }
    public LightweightCache<string, AmazonSnsTopic> Topics { get; }

    public AmazonSQSConfig SqsConfig { get; } = new();
    public AmazonSimpleNotificationServiceConfig SnsConfig { get; } = new();

    internal IAmazonSQS? SqsClient { get; private set; }
    internal IAmazonSimpleNotificationService? SnsClient { get; private set; }

    public int LocalStackPort { get; set; }

    public bool UseLocalStackInDevelopment { get; set; }
    public bool DisableDeadLetterQueues { get; set; }

    public static string SanitizeAwsName(string identifier)
    {
        //AWS requires FIFO queues and topics to have a `.fifo` suffix
        var suffixIndex = identifier.LastIndexOf(".fifo", StringComparison.OrdinalIgnoreCase);

        if (suffixIndex != -1) // ".fifo" suffix found
        {
            var prefix = identifier[..suffixIndex];
            var suffix = identifier[suffixIndex..];

            prefix = prefix.Replace('.', Separator);

            return prefix + suffix;
        }

        // ".fifo" suffix not found
        return identifier.Replace('.', Separator);
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return SanitizeAwsName(identifier);
    }

    protected override IEnumerable<Endpoint> explicitEndpoints()
    {
        foreach (var queue in Queues) yield return queue;
        foreach (var topic in Topics) yield return topic;
    }

    protected override IEnumerable<AmazonEndpoint> endpoints()
    {
        if (!DisableDeadLetterQueues)
        {
            var dlqNames = Queues.Where(x => x.IsListener).Select(x => x.DeadLetterQueueName).Where(x => x.IsNotEmpty()).Distinct().ToArray();
            foreach (var dlqName in dlqNames) Queues.FillDefault(dlqName!);
        }

        foreach (var queue in Queues) yield return queue;
        foreach (var topic in Topics) yield return topic;
    }
    
    public new Endpoint GetOrCreateEndpoint(Uri uri)
    {
        if (uri.Scheme != TopicSegment && uri.Scheme != QueueSegment)
        {
            throw new ArgumentOutOfRangeException($"Uri must have scheme '{Protocol}', but received {uri.Scheme}");
        }

        return findEndpointByUri(uri);
    }
    
    protected override AmazonEndpoint findEndpointByUri(Uri uri)
    {
        var type = uri.Host;
        var name = uri.Segments[1].TrimEnd('/');
        
        return type switch
        {
            QueueSegment => Queues.FirstOrDefault(x => x.Uri.OriginalString == uri.OriginalString) ??
                           Queues[name],
            TopicSegment => Topics.FirstOrDefault(x => x.Uri.OriginalString == uri.OriginalString) ?? 
                           Topics[name],
            _ => throw new ArgumentOutOfRangeException(nameof(uri), $"Invalid Amazon object type '{type}'")
        };
    }

    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        SqsClient ??= BuildSqsClient(runtime);
        SnsClient ??= BuildSnsClient(runtime);
        return ValueTask.CompletedTask;
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Queue Name", "name");
        yield return new PropertyColumn("Messages", nameof(GetQueueAttributesResponse.ApproximateNumberOfMessages),
            Justify.Right);
        yield return new PropertyColumn("Delayed",
            nameof(GetQueueAttributesResponse.ApproximateNumberOfMessagesDelayed), Justify.Right);
        yield return new PropertyColumn("Not Visible",
            nameof(GetQueueAttributesResponse.ApproximateNumberOfMessagesNotVisible), Justify.Right);
    }

    private IAmazonSQS BuildSqsClient(IWolverineRuntime runtime)
    {
        if (CredentialSource == null)
        {
            return new AmazonSQSClient(SqsConfig);
        }

        var credentials = CredentialSource(runtime);
        return new AmazonSQSClient(credentials, SqsConfig);
    }
    
    private IAmazonSimpleNotificationService BuildSnsClient(IWolverineRuntime runtime)
    {
        if (CredentialSource == null)
        {
            return new AmazonSimpleNotificationServiceClient(SnsConfig);
        }

        var credentials = CredentialSource(runtime);
        return new AmazonSimpleNotificationServiceClient(credentials, SnsConfig);
    }

    internal AmazonSqsQueue EndpointForQueue(string queueName)
    {
        return Queues[queueName];
    }
    
    
    internal AmazonSnsTopic EndpointForTopic(string topicName)
    {
        return Topics[topicName];
    }
    
    internal async Task<string> GetQueueArn(string queueName)
    {
        var queue = Queues[queueName];
        var atts = await queue.GetAttributesAsync();

        if (atts is null)
        {
            throw new KeyNotFoundException($"Queue '{queueName}' does not exist");
        }
        
        return atts[nameof(GetQueueAttributesResponse.QueueARN)];
    }

    internal void ConnectToLocalStack(int port = 4566)
    {
        CredentialSource = _ => new BasicAWSCredentials("ignore", "ignore");
        SqsConfig.ServiceURL = $"http://localhost:{port}";
        SnsConfig.ServiceURL = $"http://localhost:{port}";
    }
}
