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

    /// <summary>
    ///     Optional async callback to configure the <see cref="PublisherServiceApiClientBuilder" /> before it is built.
    ///     Applied after <see cref="EmulatorDetection" /> is set, so it may override any transport-level defaults.
    ///     Multiple calls compose in order. Use the async signature when credential construction requires I/O
    ///     (e.g. fetching a token from Azure Key Vault or Azure IMDS).
    /// </summary>
    public Func<PublisherServiceApiClientBuilder, ValueTask>? ConfigurePublisherApiBuilder { get; set; }

    /// <summary>
    ///     Optional async callback to configure the <see cref="SubscriberServiceApiClientBuilder" /> before it is built.
    ///     Applied after <see cref="EmulatorDetection" /> is set, so it may override any transport-level defaults.
    ///     Multiple calls compose in order. Use the async signature when credential construction requires I/O.
    /// </summary>
    public Func<SubscriberServiceApiClientBuilder, ValueTask>? ConfigureSubscriberApiBuilder { get; set; }

    /// <summary>
    ///     Optional async callback to configure the <see cref="SubscriberClientBuilder" /> before it is built.
    ///     Applied after <see cref="EmulatorDetection" /> is set, so it may override any transport-level defaults.
    ///     Multiple calls compose in order. Use the async signature when credential construction requires I/O.
    /// </summary>
    public Func<SubscriberClientBuilder, ValueTask>? ConfigureSubscriberClientBuilder { get; set; }

    public PubsubTransport() : base(ProtocolName, "Google Cloud Platform Pub/Sub", ["gcp", ProtocolName])
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
        if (string.IsNullOrWhiteSpace(ProjectId))
        {
            throw new InvalidOperationException(
                "Google Cloud Platform Pub/Sub project id must be set before connecting");
        }

        var pubBuilder = new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection
        };
        if (ConfigurePublisherApiBuilder != null)
            await ConfigurePublisherApiBuilder(pubBuilder);

        var subApiBuilder = new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection
        };
        if (ConfigureSubscriberApiBuilder != null)
            await ConfigureSubscriberApiBuilder(subApiBuilder);

        AssignedNodeNumber = runtime.DurabilitySettings.AssignedNodeNumber;
        PublisherApiClient = await pubBuilder.BuildAsync();
        SubscriberApiClient = await subApiBuilder.BuildAsync();
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

        // The per-node response endpoint must be unique to this running node. In Solo mode the
        // assigned node number is always 1 (#3188), so several Solo services sharing a project would
        // collide on the same response subscription and cross-deliver each other's replies — use the
        // always unique UniqueNodeId instead. Balanced nodes get a unique AssignedNodeNumber via
        // election, so they keep the existing node-number name. See #3189.
        var responseNode = runtime.Options.Durability.Mode == DurabilityMode.Solo
            ? runtime.Options.UniqueNodeId.ToString("N")
            : Math.Abs(runtime.DurabilitySettings.AssignedNodeNumber).ToString();
        var responseName = $"{ResponseName}.{responseNode}";
        var responseTopic = new PubsubEndpoint(responseName, this, EndpointRole.System);

        responseTopic.IsListener = responseTopic.IsUsedForReplies = true;

        Topics[responseName] = responseTopic;
    }
}