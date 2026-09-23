using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
///     GH-4556. <c>BroadcastToTopicAsync</c> was the one send path that never called
///     <c>TrackEnvelopeCorrelation</c>: <c>MessageRoute.CreateForSending</c> sets no Source, no
///     CorrelationId, no ParentId and no Store, and nothing downstream filled them in. That is also
///     why a projection side effect's <c>ToTopic()</c> carried none of its MessageMetadata -- the
///     metadata rides TrackEnvelopeCorrelation, so skipping it skipped the metadata too.
/// </summary>
public class topic_broadcasts_stamp_envelope_correlation
{
    [Fact]
    public async Task a_topic_broadcast_gets_correlation_and_source_like_every_other_send()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "TopicSender";
                opts.Discovery.DisableConventionalDiscovery();

                opts.PublishAllMessages().To("stub://topics");
                opts.Policies.Add(new TopicRoutedStubEndpointPolicy());
            }).StartAsync(TestContext.Current.CancellationToken);

        var tracked = await host
            .TrackActivity()
            .Timeout(15.Seconds())
            .ExecuteAndWaitAsync(c => c.BroadcastToTopicAsync("blue", new TopicPayload("one")).AsTask());

        var envelope = tracked.Sent.SingleEnvelope<TopicPayload>();

        envelope.TopicName.ShouldBe("blue");
        envelope.CorrelationId.ShouldNotBeNull();
        envelope.Source.ShouldBe("TopicSender");
    }
}

public record TopicPayload(string Name);

/// <summary>
///     The stub transport has no topic support of its own, so flip the endpoint's routing mode here.
///     Endpoint policies run inside Endpoint.Compile(), which is before anything asks the router for
///     a topic route -- MessageRouterBase caches _topicRoutes in its constructor.
/// </summary>
internal class TopicRoutedStubEndpointPolicy : IEndpointPolicy
{
    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        if (endpoint.Uri == new Uri("stub://topics"))
        {
            endpoint.RoutingType = RoutingMode.ByTopic;
        }
    }
}
