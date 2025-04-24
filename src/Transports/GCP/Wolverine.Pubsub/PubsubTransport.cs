using System.Text.RegularExpressions;
using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

public class PubsubTransport : BrokerTransport<PubsubEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "pubsub";
    public const string ResponseName = "wlvrn.responses";
    public const string DeadLetterName = "wlvrn.dead-letter";
    internal static Regex NameRegex = new("^(?!goog)[A-Za-z][A-Za-z0-9\\-_.~+%]{2,254}$");

    public readonly LightweightCache<string, PubsubEndpoint> Topics;

    internal int AssignedNodeNumber;
    public PubsubDeadLetterOptions DeadLetter = new();
    public EmulatorDetection EmulatorDetection = EmulatorDetection.None;

    public string ProjectId = string.Empty;
    internal PublisherServiceApiClient? PublisherApiClient;
    internal SubscriberServiceApiClient? SubscriberApiClient;

    /// <summary>
    ///     Is this transport connection allowed to build and use response topic and subscription
    ///     for just this node?
    /// </summary>
    public bool SystemEndpointsEnabled = false;

    public PubsubTransport() : base(ProtocolName, "Google Cloud Platform Pub/Sub")
    {
        IdentifierDelimiter = ".";
        Topics = new LightweightCache<string, PubsubEndpoint>(name => new PubsubEndpoint(name, this));
    }

    public PubsubTransport(string projectId) : this()
    {
        ProjectId = projectId;
    }

    public override Uri ResourceUri => new Uri("pubsub://" + ProjectId);

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        var pubBuilder = new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection
        };
        var subBuilder = new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection
        };

        if (string.IsNullOrWhiteSpace(ProjectId))
        {
            throw new InvalidOperationException(
                "Google Cloud Platform Pub/Sub project id must be set before connecting");
        }

        AssignedNodeNumber = runtime.DurabilitySettings.AssignedNodeNumber;
        PublisherApiClient = await pubBuilder.BuildAsync();
        SubscriberApiClient = await subBuilder.BuildAsync();
    }

    public override Endpoint? ReplyEndpoint()
    {
        var endpoint = base.ReplyEndpoint();

        if (endpoint is PubsubEndpoint)
        {
            return endpoint;
        }

        return null;
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield break;
    }

    protected override IEnumerable<Endpoint> explicitEndpoints()
    {
        return Topics;
    }

    protected override IEnumerable<PubsubEndpoint> endpoints()
    {
        var dlNames = Topics.Select(x => x.DeadLetterName).Where(x => x.IsNotEmpty()).Distinct().ToArray();

        foreach (var dlName in dlNames)
        {
            if (dlName.IsEmpty())
            {
                continue;
            }

            var dl = Topics[dlName];

            dl.DeadLetterName = null;
            dl.Server.Subscription.Options.DeadLetterPolicy = null;
            dl.IsDeadLetter = true;
            dl.Server.Topic.Options = DeadLetter.Topic;
            dl.Server.Subscription.Options = DeadLetter.Subscription;
        }

        return Topics;
    }

    protected override PubsubEndpoint findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != Protocol)
        {
            throw new ArgumentOutOfRangeException(nameof(uri));
        }

        return Topics.FirstOrDefault(x => x.Uri.OriginalString == uri.OriginalString) ??
               Topics[uri.Segments[1].TrimEnd('/')];
    }

    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime)
    {
        if (!SystemEndpointsEnabled)
        {
            return;
        }

        var responseName = $"{ResponseName}.{Math.Abs(runtime.DurabilitySettings.AssignedNodeNumber)}";
        var responseTopic = new PubsubEndpoint(responseName, this, EndpointRole.System);

        responseTopic.IsListener = responseTopic.IsUsedForReplies = true;

        Topics[responseName] = responseTopic;
    }
}