using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Google.Protobuf.WellKnownTypes;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

/* Notes
 
Require ProjectId upfront?
build TopicName and SubscriptionName up front
Hang the GCP Subscription off of PubsubSubscription.Configuration?
 
 
 
 */

public class PubsubTransport : BrokerTransport<PubsubEndpoint>
{
    public const string ProtocolName = "pubsub";
    public const string ResponseName = "wlvrn.responses";
    
    public LightweightCache<string, PubsubTopic> Topics { get; }
    
    public PubsubTransport() : base(ProtocolName, "Google Cloud Platform Pub/Sub")
    {
        IdentifierDelimiter = ".";
        Topics = new LightweightCache<string, PubsubTopic>(id =>
            new PubsubTopic(this, id, EndpointRole.Application));
    }
    
    public string ProjectId { get; set; } = string.Empty;    
    public EmulatorDetection EmulatorDetection { get; set; }  = EmulatorDetection.None;

    protected override IEnumerable<PubsubEndpoint> endpoints()
    {
        foreach (var topic in Topics)
        {
            yield return topic;

            foreach (var subscription in topic.GcpSubscriptions)
            {
                yield return subscription;
            }
        }
    }

    protected override PubsubEndpoint findEndpointByUri(Uri uri)
    {
        var pubsubTopic = Topics[uri.Host];
        
        if (uri.Segments.Length == 2)
        {
            return pubsubTopic.GcpSubscriptions[uri.Segments.Last().Trim('/')];
        }

        return pubsubTopic;
    }

    public override Uri ResourceUri => new Uri("pubsub://" + ProjectId);
    public bool SystemEndpointsEnabled { get; set; }

    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        return ValueTask.CompletedTask;
    }
 
    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime)
    {
        if (!SystemEndpointsEnabled)
        {
            return;
        }

        var responseName = $"{ResponseName}.{Math.Abs(runtime.DurabilitySettings.AssignedNodeNumber)}";
        var responseTopic = new PubsubTopic(this, responseName, EndpointRole.System)
        {
            MessageRetentionDuration = 1.Hours(),
            IsUsedForReplies = true,
            IsListener = false
        };

        // Need this to be able to listen
        var subscription = responseTopic.GcpSubscriptions["control"];
        subscription.MarkRoleAsSystem();
        subscription.Options.MessageRetentionDuration = Duration.FromTimeSpan(10.Minutes());
        subscription.Options.ExpirationPolicy = new ExpirationPolicy { Ttl = Duration.FromTimeSpan(1.Days()) };
        
        // Has to be the response topic because that's all you can send to
        runtime.Options.Transports.NodeControlEndpoint = responseTopic;

        Topics[responseName] = responseTopic;
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield break;
    }
    
    private PublisherServiceApiClient? _publisherApiClient;
    private SubscriberServiceApiClient? _subscriberApiClient;

    internal async Task<bool> WithPublisherServiceApiClient(Func<PublisherServiceApiClient, Task<bool>> action)
    {
        if (_publisherApiClient == null)
        {
            var builder = new PublisherServiceApiClientBuilder
            {
                EmulatorDetection = EmulatorDetection
            };

            _publisherApiClient = await builder.BuildAsync();
        }

        return await action(_publisherApiClient);
    }
    
    internal async Task<bool> WithSubscriberServiceApiClient(Func<SubscriberServiceApiClient, Task<bool>> action)
    {
        if (_subscriberApiClient == null)
        {
            var builder = new SubscriberServiceApiClientBuilder()
            {
                EmulatorDetection = EmulatorDetection
            };

            _subscriberApiClient = await builder.BuildAsync();
        }

        return await action(_subscriberApiClient);
    }
}