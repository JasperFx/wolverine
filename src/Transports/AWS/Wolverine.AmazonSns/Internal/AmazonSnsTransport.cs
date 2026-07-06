using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using JasperFx.Core;
using JasperFx.Descriptors;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSns.Internal;

public class AmazonSnsTransport : BrokerTransport<AmazonSnsTopic>, IAsyncDisposable
{
    public const string SnsProtocol = "sns";

    public const char Separator = '-';

    public AmazonSnsTransport(string protocol) : base(protocol, "Amazon SNS", ["aws", "sns"])
    {
        Topics = new LightweightCache<string, AmazonSnsTopic>(name => new AmazonSnsTopic(name, this));

        IdentifierDelimiter = "-";
    }

    public AmazonSnsTransport() : this(SnsProtocol)
    {
    }

    internal AmazonSnsTransport(IAmazonSimpleNotificationService snsClient, IAmazonSQS sqsClient) : this()
    {
        SnsClient = snsClient;
        SqsClient = sqsClient;
    }

    public override Uri ResourceUri
    {
        get
        {
            // An explicitly set ServiceURL (e.g. LocalStack) wins
            if (SnsConfig.ServiceURL.IsNotEmpty())
            {
                return new Uri(SnsConfig.ServiceURL);
            }

            // Otherwise fall back to the configured region so that this purely
            // diagnostic Uri doesn't throw when only RegionEndpoint was set
            try
            {
                var region = SnsConfig.RegionEndpoint?.SystemName;
                if (region.IsNotEmpty())
                {
                    return new Uri($"https://sns.{region}.amazonaws.com");
                }
            }
            catch (Exception)
            {
                // RegionEndpoint resolution can probe ambient configuration; ignore and
                // use the generic fallback below
            }

            return new Uri("sns://amazon");
        }
    }

    [DescribeAsConfigurationState]
    public Func<IWolverineRuntime, AWSCredentials>? CredentialSource { get; set; }
    [IgnoreDescription]
    public LightweightCache<string, AmazonSnsTopic> Topics { get; }

    /// <summary>
    /// Broker-per-tenant registrations (GH-3305). Each tenant owns a child transport pointed at its own SNS
    /// account/region/endpoint (and its own paired SQS client for subscription provisioning); outbound is routed by
    /// <see cref="Envelope.TenantId"/> through a <see cref="Wolverine.Transports.Sending.TenantedSender"/>. SNS is
    /// publish-only, so there is no per-tenant listener — inbound tenant traffic is consumed by the paired per-tenant
    /// SQS subscriptions (see the Amazon SQS broker-per-tenant support).
    /// </summary>
    [IgnoreDescription]
    internal LightweightCache<string, AmazonSnsTenant> Tenants { get; } = new(name => new AmazonSnsTenant(name));

    [ChildDescription]
    public AmazonSimpleNotificationServiceConfig SnsConfig { get; } = new();
    internal IAmazonSimpleNotificationService? SnsClient { get; set; }
    internal IAmazonSQS? SqsClient { get; set; }

    public override string? DescribeEndpoint()
    {
        // An explicit ServiceURL (e.g. LocalStack) is a plain endpoint URL; AWS credentials are supplied separately.
        if (SnsConfig.ServiceURL.IsNotEmpty()) return SnsConfig.ServiceURL;

        try
        {
            var region = SnsConfig.RegionEndpoint?.SystemName;
            if (region.IsNotEmpty()) return region;
        }
        catch (Exception)
        {
            // RegionEndpoint resolution can probe ambient configuration; ignore.
        }

        return null;
    }

    public int LocalStackPort { get; set; }

    public bool UseLocalStackInDevelopment { get; set; }
    internal AmazonSqsTransport SQS { get; set; } = null!;

    /// <summary>
    /// True when <see cref="SQS"/> is a standalone helper transport (named-broker case, GH-3305) rather than a
    /// transport registered in the <see cref="TransportCollection"/>. When true, its connection config is seeded
    /// from this transport's own SNS connection in <see cref="ConnectAsync"/> so the subscription-provisioning
    /// client targets the same account/region as the named SNS broker.
    /// </summary>
    internal bool PairedSqsIsStandalone { get; set; }

    // TODO duplicated code in SqsTransport
    public static string SanitizeSnsName(string identifier)
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
        return SanitizeSnsName(identifier);
    }
    
    protected override IEnumerable<AmazonSnsTopic> endpoints()
    {
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
        // Named broker (GH-3305): the standalone paired SQS transport is not user-configured, so align its
        // connection with this SNS broker's own so subscription provisioning targets the same account/region.
        if (PairedSqsIsStandalone)
        {
            if (SnsConfig.ServiceURL.IsNotEmpty())
            {
                SQS.Config.ServiceURL = SnsConfig.ServiceURL;
            }
            else if (SnsConfig.RegionEndpoint != null)
            {
                SQS.Config.RegionEndpoint = SnsConfig.RegionEndpoint;
            }

            SQS.Config.AuthenticationRegion = SnsConfig.AuthenticationRegion;
        }

        SnsClient ??= BuildSnsClient(runtime);
        SqsClient ??= BuildSqsClient(runtime);

        // Broker-per-tenant (GH-3305): now that the parent connection is fully resolved, seed each tenant's child
        // transport from it and build the tenant's own SNS + paired SQS clients. There is no listener to start (SNS
        // is publish-only); the tenant's topic + subscriptions are provisioned lazily on first publish through the
        // tenant's own clients (AmazonSnsTopic.InitializeAsync).
        foreach (var tenant in Tenants)
        {
            tenant.Compile(this, runtime);
        }

        return ValueTask.CompletedTask;
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        throw new NotImplementedException();
    }
    
    internal AmazonSnsTopic EndpointForTopic(string topicName)
    {
        return Topics[topicName];
    }
    
    internal void ConnectToLocalStack(int port = 4566)
    {
        CredentialSource = _ => new BasicAWSCredentials("ignore", "ignore");
        SnsConfig.ServiceURL = $"http://localhost:{port}";
        SQS.Config.ServiceURL = $"http://localhost:{port}";
    }
    
    internal IAmazonSimpleNotificationService BuildSnsClient(IWolverineRuntime runtime)
    {
        if (CredentialSource == null)
        {
            return new AmazonSimpleNotificationServiceClient(SnsConfig);
        }

        var credentials = CredentialSource(runtime);
        return new AmazonSimpleNotificationServiceClient(credentials, SnsConfig);
    }

    internal AmazonSQSClient BuildSqsClient(IWolverineRuntime runtime)
    {
        if (CredentialSource == null)
        {
            return new AmazonSQSClient(SQS.Config);
        }

        var credentials = CredentialSource(runtime);
        return new AmazonSQSClient(credentials, SQS.Config);
    }

    public async ValueTask DisposeAsync()
    {
        SnsClient?.Dispose();
        SqsClient?.Dispose();

        // Broker-per-tenant (GH-3305): each tenant owns its own SNS + paired SQS clients through its child transport.
        foreach (var tenant in Tenants)
        {
            await tenant.Transport.DisposeAsync();
        }
    }

    /// <summary>
    /// Override to customize the queue policy for permissions for an AWS SQS queue that subscribes to
    /// an SNS topic
    /// </summary>
    [IgnoreDescription]
    public Func<SqsTopicDescription, string> QueuePolicyBuilder { get; set; } = description =>
    {
        var queuePolicy = $$"""
                            {
                              "Version": "2012-10-17",
                              "Statement": [{
                                  "Effect": "Allow",
                                  "Principal": {
                                      "Service": "sns.amazonaws.com"
                                  },
                                  "Action": "sqs:SendMessage",
                                  "Resource": "{{description.QueueArn}}",
                                  "Condition": {
                                    "ArnEquals": {
                                        "aws:SourceArn": "{{description.TopicArn}}"
                                    }
                                  }
                              }]
                            }
                            """;

        return queuePolicy;
    };
}
