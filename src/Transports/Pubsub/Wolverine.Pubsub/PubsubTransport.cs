using Wolverine.Transports;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using Wolverine.Configuration;

namespace Wolverine.Pubsub;

public class PubsubTransport : BrokerTransport<PubsubEndpoint>, IAsyncDisposable {
    public const string ProtocolName = "pubsub";
    public const string ResponseEndpointName = "pubsub-response";
    public const string RetryEndpointName = "pubsub-retry";
    public const string DeadLetterEndpointName = "wolverine-dead-letter";

    internal PublisherServiceApiClient? PublisherApiClient = null;
    internal SubscriberServiceApiClient? SubscriberApiClient = null;
    internal PubsubTopic? RetryTopic { get; set; }


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
        IdentifierDelimiter = ".";

        Topics = new(name => new(name, this));
    }

    public PubsubTransport(string projectId, PubsubTransportOptions? options = null) : this() {
        ProjectId = projectId;
        Options = options ?? Options;
    }

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
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected override IEnumerable<Endpoint> explicitEndpoints() {
        foreach (var topic in Topics) yield return topic;
        foreach (var subscription in Subscriptions) yield return subscription;
    }

    protected override IEnumerable<PubsubEndpoint> endpoints() {
        if (!Options.DisableDeadLetter) {
            var dlNames = Subscriptions.Select(x => x.Options.DeadLetterName).Where(x => x.IsNotEmpty()).Distinct().ToArray();

            foreach (var dlName in dlNames) Topics[dlName!].FindOrCreateSubscription();
        }

        foreach (var topic in Topics) yield return topic;
        foreach (var subscription in Subscriptions) yield return subscription;
    }

    protected override PubsubEndpoint findEndpointByUri(Uri uri) {
        var topicName = uri.Segments[1].TrimEnd('/');

        if (uri.Segments.Length == 3) {
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

        var responsesName = $"wolverine.responses.{Math.Abs(runtime.DurabilitySettings.AssignedNodeNumber)}";
        var responsesTopic = new PubsubTopic(responsesName, this, EndpointRole.System);
        var responsesSubscription = new PubsubSubscription(responsesName, responsesTopic, this, EndpointRole.System);

        responsesSubscription.Mode = EndpointMode.BufferedInMemory;
        responsesSubscription.EndpointName = ResponseEndpointName;
        responsesSubscription.IsUsedForReplies = true;

        Topics[responsesName] = responsesTopic;
        Subscriptions.Add(responsesSubscription);

        var retryName = SanitizeIdentifier($"wolverine.retries.{runtime.Options.ServiceName}".ToLower());
        var retryTopic = new PubsubTopic(retryName, this, EndpointRole.System);
        var retrySubscription = new PubsubSubscription(retryName, retryTopic, this, EndpointRole.System);

        retrySubscription.Mode = EndpointMode.BufferedInMemory;
        retrySubscription.EndpointName = RetryEndpointName;

        Topics[retryName] = retryTopic;
        Subscriptions.Add(retrySubscription);
        RetryTopic = retryTopic;
    }
}
