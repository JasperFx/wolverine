using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.Runtime.Serialization;

namespace Wolverine.AmazonSns.Tests;

/// <summary>
///     GH-3793, end to end against a real FIFO topic with <c>ContentBasedDeduplication</c> turned off.
///     Both cases below used to fail deterministically with
///     "Invalid parameter: The topic should either have ContentBasedDeduplication enabled or
///     MessageDeduplicationId provided explicitly", which is the failure the reporter hit on every
///     envelope recovered out of the durable outbox after an outage.
/// </summary>
public class fifo_topic_publishing_3793 : IAsyncLifetime
{
    private const string TopicName = "gh3793_fifo.fifo";

    private IHost _host = null!;
    private AmazonSnsTopic _topic = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSnsTransportLocally().AutoProvision();

                opts.PublishMessage<FifoMessage>()
                    .ToSnsTopic(TopicName)
                    .ConfigureTopicCreation(request =>
                    {
                        request.Attributes ??= new Dictionary<string, string>();
                        request.Attributes["FifoTopic"] = "true";
                        request.Attributes["ContentBasedDeduplication"] = "false";
                    });
            }).StartAsync();

        _topic = _host.Services.GetRequiredService<WolverineOptions>()
            .AmazonSnsTransport().Topics.Single(x => x.TopicName == TopicName);

        await _topic.InitializeAsync(NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _topic.TeardownAsync(NullLogger.Instance);
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task an_envelope_recovered_from_durable_storage_can_still_be_published()
    {
        var envelope = new Envelope
        {
            Data = [1, 2, 3],
            MessageType = "probe-message",
            GroupId = "group-under-test",
            DeduplicationId = Guid.NewGuid().ToString(),
            Destination = _topic.Uri
        };

        // This is the outbox round trip: recovery after a restart, or reassignment while the
        // sending agent is latched, both hand the sender an envelope rebuilt from these bytes.
        var recovered = EnvelopeSerializer.Deserialize(EnvelopeSerializer.Serialize(envelope));

        recovered.DeduplicationId.ShouldBe(envelope.DeduplicationId);

        await Should.NotThrowAsync(() => _topic.SendMessageAsync(recovered, NullLogger.Instance));
    }

    [Fact]
    public async Task the_circuit_resume_ping_can_be_published()
    {
        // The latched sender probes with this before it will unlatch, so if the ping can't be
        // published the endpoint stays latched forever no matter how healthy SNS is.
        var ping = Envelope.ForPing(_topic.Uri);

        await Should.NotThrowAsync(() => _topic.SendMessageAsync(ping, NullLogger.Instance));
    }
}

public record FifoMessage(string Name);
