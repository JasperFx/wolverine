using System.Text.RegularExpressions;
using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using JasperFx.Descriptors;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
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
    /// Registered tenants for the broker-per-tenant model (GH-3306). Each tenant is served by its own dedicated
    /// GCP project/connection while sharing the endpoint topology. Keyed by tenant id.
    /// </summary>
    [IgnoreDescription]
    public LightweightCache<string, PubsubTenant> Tenants { get; } = new();

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
    [IgnoreDescription]
    public Func<PublisherServiceApiClientBuilder, ValueTask>? ConfigurePublisherApiBuilder { get; set; }

    /// <summary>
    ///     Optional async callback to configure the <see cref="SubscriberServiceApiClientBuilder" /> before it is built.
    ///     Applied after <see cref="EmulatorDetection" /> is set, so it may override any transport-level defaults.
    ///     Multiple calls compose in order. Use the async signature when credential construction requires I/O.
    /// </summary>
    [IgnoreDescription]
    public Func<SubscriberServiceApiClientBuilder, ValueTask>? ConfigureSubscriberApiBuilder { get; set; }

    /// <summary>
    ///     Optional async callback to configure the <see cref="SubscriberClientBuilder" /> before it is built.
    ///     Applied after <see cref="EmulatorDetection" /> is set, so it may override any transport-level defaults.
    ///     Multiple calls compose in order. Use the async signature when credential construction requires I/O.
    /// </summary>
    [IgnoreDescription]
    public Func<SubscriberClientBuilder, ValueTask>? ConfigureSubscriberClientBuilder { get; set; }

    /// <summary>
    /// Build the Google Cloud Platform Pub/Sub transport. The single-string constructor sets the transport
    /// <b>protocol</b> (broker name / URI scheme), NOT the project id — this is required so that
    /// <see cref="Wolverine.Transports.TransportCollection.GetOrCreate{T}(Wolverine.BrokerName)" /> can spin up
    /// named brokers via <c>Activator.CreateInstance(typeof(PubsubTransport), name.Name)</c>. Set the
    /// <see cref="ProjectId" /> separately via <c>UsePubsub</c>/<c>AddNamedPubsubBroker</c>.
    /// </summary>
    public PubsubTransport(string protocol) : base(protocol, "Google Cloud Platform Pub/Sub", ["gcp", protocol])
    {
        IdentifierDelimiter = ".";
        Topics = new LightweightCache<string, PubsubEndpoint>(name => new PubsubEndpoint(name, this));
    }

    public PubsubTransport() : this(ProtocolName)
    {
    }

    public override Uri ResourceUri => new Uri($"{Protocol}://" + ProjectId);

    /// <summary>
    /// The resolved API client set for the default/shared connection. Senders and listeners that are not bound to a
    /// specific tenant use this.
    /// </summary>
    internal PubsubClientSet DefaultClients => new()
    {
        ProjectId = ProjectId,
        EmulatorDetection = EmulatorDetection,
        PublisherApiClient = PublisherApiClient,
        SubscriberApiClient = SubscriberApiClient,
        ConfigureSubscriberClientBuilder = ConfigureSubscriberClientBuilder
    };

    /// <summary>
    /// Resolve the API client set for a tenant (broker-per-tenant). Built during <see cref="ConnectAsync" />.
    /// </summary>
    internal PubsubClientSet GetTenantClients(PubsubTenant tenant)
    {
        return tenant.Clients ?? throw new WolverinePubsubTransportNotConnectedException();
    }

    public override string? DescribeEndpoint()
    {
        if (string.IsNullOrWhiteSpace(ProjectId)) return null;
        return EmulatorDetection == EmulatorDetection.None
            ? $"project: {ProjectId}"
            : $"project: {ProjectId} (emulator)";
    }

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

        // Broker-per-tenant (GH-3306): every registered tenant builds its own dedicated client pair against its own
        // GCP project. Credential/emulator hooks are seeded from the parent transport when the tenant did not set
        // its own, so a tenant only re-points the axes it actually overrides.
        foreach (var tenant in Tenants)
        {
            tenant.EmulatorDetection = tenant.EmulatorDetection == EmulatorDetection.None
                ? EmulatorDetection
                : tenant.EmulatorDetection;
            tenant.ConfigurePublisherApiBuilder ??= ConfigurePublisherApiBuilder;
            tenant.ConfigureSubscriberApiBuilder ??= ConfigureSubscriberApiBuilder;
            tenant.ConfigureSubscriberClientBuilder ??= ConfigureSubscriberClientBuilder;

            await tenant.ConnectAsync();
        }
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