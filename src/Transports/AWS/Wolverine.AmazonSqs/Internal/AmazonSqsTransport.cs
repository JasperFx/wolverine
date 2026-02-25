using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSqsTransport : BrokerTransport<AmazonSqsQueue>
{
    public const string DeadLetterQueueName = DeadLetterQueueConstants.DefaultQueueName;
    public const string ResponseEndpointName = "AmazonSqsResponses";
    public const char Separator = '-';

    private static readonly TimeSpan OrphanThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(2);
    private const string LastActiveTagKey = "wolverine:last-active";

    internal readonly List<AmazonSqsQueue> SystemQueues = new();
    private Task? _keepAliveTask;

    public AmazonSqsTransport(string protocol) : base(protocol, "Amazon SQS", ["aws", "sqs"])
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

    /// <summary>
    /// Is this transport connection allowed to build and use response and control queues
    /// for just this node? Default is false, requiring explicit opt-in.
    /// </summary>
    public bool SystemQueuesEnabled { get; set; }

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

    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime)
    {
        if (!SystemQueuesEnabled) return;

        // Lowercase the name because Uri normalizes the host portion to lowercase,
        // and SQS queue names are case-sensitive. Without this, the sender creates
        // "wolverine-response-MyApp-123" but the receiver resolves the reply URI
        // to "wolverine-response-myapp-123" (lowercased by Uri), creating a different queue.
        var responseName = SanitizeSqsName(
            $"wolverine.response.{runtime.Options.ServiceName}.{runtime.DurabilitySettings.AssignedNodeNumber}")
            .ToLowerInvariant();

        var queue = Queues[responseName];
        queue.Mode = EndpointMode.BufferedInMemory;
        queue.IsListener = true;
        queue.EndpointName = ResponseEndpointName;
        queue.IsUsedForReplies = true;
        queue.Role = EndpointRole.System;
        queue.DeadLetterQueueName = null;
        queue.Configuration.Attributes ??= new Dictionary<string, string>();
        queue.Configuration.Attributes["MessageRetentionPeriod"] = "300";

        SystemQueues.Add(queue);
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        Client ??= BuildClient(runtime);

        if (SystemQueuesEnabled)
        {
            await CleanupOrphanedSystemQueuesAsync(runtime);
            StartSystemQueueKeepAlive(runtime.DurabilitySettings.Cancellation, runtime);
        }
    }

    internal async Task CleanupOrphanedSystemQueuesAsync(IWolverineRuntime runtime)
    {
        var logger = runtime.LoggerFactory.CreateLogger<AmazonSqsTransport>();
        var prefixes = new[] { "wolverine-response-", "wolverine-control-" };

        foreach (var prefix in prefixes)
        {
            try
            {
                var response = await Client!.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = prefix });

                foreach (var queueUrl in response.QueueUrls)
                {
                    try
                    {
                        var tags = await Client.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = queueUrl });

                        if (tags.Tags.TryGetValue(LastActiveTagKey, out var lastActiveStr)
                            && DateTimeOffset.TryParse(lastActiveStr, out var lastActive))
                        {
                            if (DateTimeOffset.UtcNow - lastActive > OrphanThreshold)
                            {
                                await Client.DeleteQueueAsync(new DeleteQueueRequest(queueUrl));
                                logger.LogInformation("Deleted orphaned Wolverine system queue {QueueUrl}", queueUrl);
                            }
                        }
                        else
                        {
                            // No valid tag — consider it orphaned
                            await Client.DeleteQueueAsync(new DeleteQueueRequest(queueUrl));
                            logger.LogInformation("Deleted untagged Wolverine system queue {QueueUrl}", queueUrl);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning(e, "Error checking orphaned queue {QueueUrl}", queueUrl);
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Error listing queues with prefix {Prefix}", prefix);
            }
        }
    }

    internal async Task TagSystemQueueAsync(string queueUrl)
    {
        await Client!.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = queueUrl,
            Tags = new Dictionary<string, string>
            {
                [LastActiveTagKey] = DateTime.UtcNow.ToString("o")
            }
        });
    }

    internal void StartSystemQueueKeepAlive(CancellationToken cancellation, IWolverineRuntime runtime)
    {
        if (_keepAliveTask != null) return;

        var logger = runtime.LoggerFactory.CreateLogger<AmazonSqsTransport>();

        _keepAliveTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(KeepAliveInterval);
            while (await timer.WaitForNextTickAsync(cancellation))
            {
                foreach (var queue in SystemQueues)
                {
                    if (queue.QueueUrl.IsNotEmpty())
                    {
                        try
                        {
                            await TagSystemQueueAsync(queue.QueueUrl!);
                        }
                        catch (Exception e)
                        {
                            logger.LogWarning(e, "Error refreshing keep-alive tag for system queue {QueueName}", queue.QueueName);
                        }
                    }
                }
            }
        }, cancellation);
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