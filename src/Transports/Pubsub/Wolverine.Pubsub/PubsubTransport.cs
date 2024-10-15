using Wolverine.Transports;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using Wolverine.Configuration;
using Spectre.Console;

namespace Wolverine.Pubsub;

public class PubsubTransport : BrokerTransport<PubsubEndpoint>, IAsyncDisposable {
    public const char Separator = '-';
    public const string ProtocolName = "pubsub";
    public const string ResponseEndpointName = "wlvrn-responses";
    public const string DeadLetterEndpointName = "wlvrn-dead-letter";

    public static string SanitizePubsubName(string identifier) => (!identifier.StartsWith("wlvrn-") ? $"wlvrn-{identifier}" : identifier).ToLowerInvariant().Replace('.', Separator);

    internal PublisherServiceApiClient? PublisherApiClient = null;
    internal SubscriberServiceApiClient? SubscriberApiClient = null;


    public string ProjectId = string.Empty;
    public readonly PubsubTransportOptions Options = new();
    public readonly LightweightCache<string, PubsubTopic> Topics;
    public readonly List<PubsubSubscription> Subscriptions = new();

    /// <summary>
    /// Is this transport connection allowed to build and use response and retry queues
    /// for just this node?
    /// </summary>
    public bool SystemEndpointsEnabled { get; set; } = true;

    public PubsubTransport() : base(ProtocolName, "Google Cloud Pub/Sub") {
        IdentifierDelimiter = "-";

        Topics = new(name => new(name, this));
    }

    public PubsubTransport(string projectId, PubsubTransportOptions? options = null) : this() {
        ProjectId = projectId;
        Options = options ?? Options;
    }

    public override string SanitizeIdentifier(string identifier) => SanitizePubsubName(identifier);

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime) {
        var pubBuilder = new PublisherServiceApiClientBuilder {
            EmulatorDetection = Options.EmulatorDetection
        };
        var subBuilder = new SubscriberServiceApiClientBuilder {
            EmulatorDetection = Options.EmulatorDetection,
        };

        if (string.IsNullOrWhiteSpace(ProjectId)) throw new InvalidOperationException("Google Cloud Pub/Sub project id must be set before connecting");

        PublisherApiClient = await pubBuilder.BuildAsync();
        SubscriberApiClient = await subBuilder.BuildAsync();
    }

    public override Endpoint? ReplyEndpoint() {
        var replies = base.ReplyEndpoint();

        if (replies is PubsubTopic) return replies;

        return null;
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns() {
        yield return new PropertyColumn("Subscription name", "name");
        yield return new PropertyColumn("Messages", "count", Justify.Right);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected override IEnumerable<Endpoint> explicitEndpoints() {
        foreach (var topic in Topics) yield return topic;
        foreach (var subscription in Subscriptions) yield return subscription;
    }

    protected override IEnumerable<PubsubEndpoint> endpoints() {
        // if (!Options.DisableDeadLetter) {
        //     var dlNames = Subscriptions.Select(x => x.Options.DeadLetterName).Where(x => x.IsNotEmpty()).Distinct().ToArray();

        //     foreach (var dlName in dlNames) Topics[dlName!].FindOrCreateSubscription();
        // }

        foreach (var topic in Topics) yield return topic;
        foreach (var subscription in Subscriptions) yield return subscription;
    }

    protected override PubsubEndpoint findEndpointByUri(Uri uri) {
        if (uri.Scheme != Protocol) throw new ArgumentOutOfRangeException(nameof(uri));

        var topicName = uri.Host;

        if (uri.Segments.Length == 2) {
            var subscription = Subscriptions.FirstOrDefault(x => x.Uri == uri);

            if (subscription != null) return subscription;

            var subscriptionName = uri.Segments.Last().TrimEnd('/');
            var topic = Topics[topicName];

            subscription = new PubsubSubscription(subscriptionName, topic, this);

            Subscriptions.Add(subscription);

            return subscription;
        }

        return Topics[topicName];
    }

    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime) {
        if (!SystemEndpointsEnabled) return;

        var responseName = SanitizeIdentifier($"{ResponseEndpointName}-{Math.Abs(runtime.DurabilitySettings.AssignedNodeNumber)}");
        var responseTopic = new PubsubTopic(responseName, this, EndpointRole.System);
        var responseSubscription = new PubsubSubscription(responseName, responseTopic, this, EndpointRole.System);

        responseSubscription.Mode = EndpointMode.BufferedInMemory;
        responseSubscription.EndpointName = ResponseEndpointName;
        responseSubscription.IsUsedForReplies = true;

        Topics[responseName] = responseTopic;
        Subscriptions.Add(responseSubscription);
    }
}
