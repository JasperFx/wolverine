using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSqsTransport : BrokerTransport<AmazonSqsQueue>
{
    public const string DeadLetterQueueName = "wolverine-dead-letter-queue";

    public const char Separator = '-';

    public AmazonSqsTransport(string protocol) : base(protocol, "Amazon SQS")
    {
        Queues = new LightweightCache<string, AmazonSqsQueue>(name => new AmazonSqsQueue(name, this));
        IdentifierDelimiter = "-";
    }

    public AmazonSqsTransport() : this("sqs")
    {

    }

    public override Uri ResourceUri => new Uri(Config.ServiceURL);

    internal AmazonSqsTransport(IAmazonSQS client) : this()
    {
        Client = client;
    }

    public Func<IWolverineRuntime, AWSCredentials>? CredentialSource { get; set; }

    public LightweightCache<string, AmazonSqsQueue> Queues { get; }

    public AmazonSQSConfig Config { get; } = new();

    internal IAmazonSQS? Client { get; private set; }

    public int LocalStackPort { get; set; }

    public bool UseLocalStackInDevelopment { get; set; }
    public bool DisableDeadLetterQueues { get; set; }

    public static string SanitizeSqsName(string identifier)
    {
        //AWS requires FIFO queues to have a `.fifo` suffix
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
        return SanitizeSqsName(identifier);
    }

    protected override IEnumerable<Endpoint> explicitEndpoints()
    {
        return Queues;
    }

    protected override IEnumerable<AmazonSqsQueue> endpoints()
    {
        if (!DisableDeadLetterQueues)
        {
            var dlqNames = Queues.Where(x => x.IsListener).Select(x => x.DeadLetterQueueName).Where(x => x.IsNotEmpty()).Distinct().ToArray();
            foreach (var dlqName in dlqNames) Queues.FillDefault(dlqName!);
        }

        return Queues;
    }

    protected override AmazonSqsQueue findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != Protocol)
        {
            throw new ArgumentOutOfRangeException(nameof(uri));
        }
        return Queues.Where(x => x.Uri.OriginalString == uri.OriginalString).FirstOrDefault() ?? Queues[uri.OriginalString.Split("//")[1].TrimEnd('/')];
    }

    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        Client ??= BuildClient(runtime);
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

    public string ServerHost => Config.ServiceURL?.ToUri().Host;
}