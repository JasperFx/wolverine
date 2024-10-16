using Wolverine.Transports;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using Wolverine.Configuration;
using Spectre.Console;
using Google.Api.Gax;

namespace Wolverine.Pubsub;

public class PubsubTransport : BrokerTransport<PubsubEndpoint>, IAsyncDisposable {
    public const char Separator = '-';
    public const string ProtocolName = "pubsub";
    public const string ResponseName = "wlvrn-responses";
    public const string DeadLetterName = "wlvrn-dead-letter";

    public static string SanitizePubsubName(string identifier) => (!identifier.StartsWith("wlvrn-") ? $"wlvrn-{identifier}" : identifier).ToLowerInvariant().Replace('.', Separator);

    internal PublisherServiceApiClient? PublisherApiClient = null;
    internal SubscriberServiceApiClient? SubscriberApiClient = null;


    public readonly LightweightCache<string, PubsubTopic> Topics;
    public readonly List<PubsubSubscription> Subscriptions = new();

    public string ProjectId = string.Empty;
    public EmulatorDetection EmulatorDetection = EmulatorDetection.None;
    public bool EnableDeadLettering = false;

    /// <summary>
    /// Is this transport connection allowed to build and use response and retry queues
    /// for just this node?
    /// </summary>
    public bool SystemEndpointsEnabled = false;

    public PubsubTransport() : base(ProtocolName, "Google Cloud Pub/Sub") {
        IdentifierDelimiter = "-";
        Topics = new(name => new(name, this));
    }

    public PubsubTransport(string projectId) : this() {
        ProjectId = projectId;
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime) {
        var pubBuilder = new PublisherServiceApiClientBuilder {
            EmulatorDetection = EmulatorDetection
        };
        var subBuilder = new SubscriberServiceApiClientBuilder {
            EmulatorDetection = EmulatorDetection,
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
        if (EnableDeadLettering) {
            var dlNames = Subscriptions.Select(x => x.DeadLetterName).Where(x => x.IsNotEmpty()).Distinct().ToArray();

            foreach (var dlName in dlNames) Topics[dlName!].FindOrCreateSubscription();
        }

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

        var responseName = SanitizeIdentifier($"{ResponseName}-{Math.Abs(runtime.DurabilitySettings.AssignedNodeNumber)}");
        var responseTopic = new PubsubTopic(responseName, this, EndpointRole.System);
        var responseSubscription = new PubsubSubscription(responseName, responseTopic, this, EndpointRole.System);

        responseSubscription.Mode = EndpointMode.BufferedInMemory;
        responseSubscription.EndpointName = ResponseName;
        responseSubscription.IsUsedForReplies = true;

        Topics[responseName] = responseTopic;
        Subscriptions.Add(responseSubscription);
    }
}
