using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSns.Internal;

public class AmazonSnsTransport : BrokerTransport<AmazonSnsTopic>
{
    public const string DeadLetterQueueName = "wolverine-dead-letter-queue";

    public const char Separator = '-';
    
    public AmazonSnsTransport() : base("sns", "Amazon SNS")
    {
        Topics = new LightweightCache<string, AmazonSnsTopic>(name => new AmazonSnsTopic(name, this));
        IdentifierDelimiter = "-";
    }

    internal AmazonSnsTransport(IAmazonSimpleNotificationService client) : this()
    {
        Client = client;
    }
    
    public Func<IWolverineRuntime, AWSCredentials>? CredentialSource { get; set; }
    
    public LightweightCache<string, AmazonSnsTopic> Topics { get; }

    public AmazonSimpleNotificationServiceConfig Config { get; } = new();

    internal IAmazonSimpleNotificationService? Client { get; private set; }
    
    public int LocalStackPort { get; set; }

    public bool UseLocalStackInDevelopment { get; set; }
    
    // TODO code is duplicated in AmazonSqsTransport, move somewhere else?
    public static string SanitizeSqsName(string identifier)
    {
        //AWS requires FIFO topics to have a `.fifo` suffix
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
        return Topics;
    }
    
    protected override IEnumerable<AmazonSnsTopic> endpoints()
    {
        // TODO will sns support dead letter queues? It would need to depend on another transport
        return Topics;
    }

    protected override AmazonSnsTopic findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != Protocol)
        {
            throw new ArgumentOutOfRangeException(nameof(uri));
        }
        return Topics.FirstOrDefault(x => x.Uri.OriginalString == uri.OriginalString) ?? Topics[uri.OriginalString.Split("//")[1].TrimEnd('/')];
    }

    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        Client ??= BuildClient(runtime);
        return ValueTask.CompletedTask;
    }
    
    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        // TODO Diagnostic columns
        throw new NotImplementedException();
    }
    
    public IAmazonSimpleNotificationService BuildClient(IWolverineRuntime runtime)
    {
        if (CredentialSource == null)
        {
            return new AmazonSimpleNotificationServiceClient(Config);
        }

        var credentials = CredentialSource(runtime);
        return new AmazonSimpleNotificationServiceClient(credentials, Config);
    }
    
    internal AmazonSnsTopic EndpointForTopic(string topicName)
    {
        return Topics[topicName];
    }
    
    internal void ConnectToLocalStack(int port = 4566)
    {
        CredentialSource = _ => new BasicAWSCredentials("ignore", "ignore");
        Config.ServiceURL = $"http://localhost:{port}";
    }
}
