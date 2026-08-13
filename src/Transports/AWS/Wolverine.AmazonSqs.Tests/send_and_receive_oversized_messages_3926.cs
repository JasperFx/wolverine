using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Tracking;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// GH-3926. End to end over a real (LocalStack) queue: a message whose body is well past SQS's 256KB
/// limit is split on the way out and put back together on the way in. A single listening node, which
/// is one of the three topologies where in-memory reassembly is safe.
/// </summary>
public class OversizedMessageFixture : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToSqsQueue("oversized_3926");

                opts.PublishAllMessages().ToSqsQueue("oversized_3926")
                    .FragmentOversizedMessages();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public class send_and_receive_oversized_messages_3926 : IClassFixture<OversizedMessageFixture>
{
    private readonly IHost _host;

    // One host for the whole class rather than one per test. AutoPurgeOnStartup calls SQS PurgeQueue,
    // and SQS allows that only once every 60 seconds -- a queue purged again inside that window swallows
    // messages sent during it, which makes a per-test host look like a broken transport.
    public send_and_receive_oversized_messages_3926(OversizedMessageFixture fixture)
    {
        _host = fixture.Host;
    }

    private static BigMessage bigMessageOf(int bytes)
    {
        // Random-ish content rather than a repeated character, so a fragment landing in the wrong slot
        // cannot pass unnoticed.
        var chars = new char[bytes];
        for (var i = 0; i < bytes; i++)
        {
            chars[i] = (char)('a' + i % 26);
        }

        return new BigMessage(Guid.NewGuid(), new string(chars));
    }

    [Fact]
    public async Task send_and_receive_a_message_over_the_sqs_limit()
    {
        // ~400KB of payload, which is over the 256KB cap before the envelope framing and base64 are
        // even applied
        var message = bigMessageOf(400 * 1024);

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(60.Seconds())
            .SendMessageAndWaitAsync(message);

        var received = session.Received.SingleMessage<BigMessage>();
        received.Id.ShouldBe(message.Id);
        received.Contents.Length.ShouldBe(message.Contents.Length);
        received.Contents.ShouldBe(message.Contents);
    }

    [Fact]
    public async Task an_ordinary_message_is_unaffected_on_a_fragmenting_endpoint()
    {
        var message = bigMessageOf(100);

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(60.Seconds())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<BigMessage>().Id.ShouldBe(message.Id);
    }

    [Fact]
    public async Task two_oversized_messages_in_flight_at_once_do_not_cross_contaminate()
    {
        var one = bigMessageOf(400 * 1024);
        var two = bigMessageOf(350 * 1024);

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(60.Seconds())
            .ExecuteAndWaitAsync(bus => Task.WhenAll(bus.PublishAsync(one).AsTask(),
                bus.PublishAsync(two).AsTask()));

        var received = session.Received.MessagesOf<BigMessage>().ToDictionary(x => x.Id);

        received.Count.ShouldBe(2);
        received[one.Id].Contents.ShouldBe(one.Contents);
        received[two.Id].Contents.ShouldBe(two.Contents);
    }

    [Fact]
    public async Task every_fragment_is_deleted_once_the_message_is_handled()
    {
        var message = bigMessageOf(400 * 1024);

        await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(60.Seconds())
            .SendMessageAndWaitAsync(message);

        // Completing a reassembled envelope has to delete every SQS message that carried it. Missing one
        // leaves an orphan fragment to reappear at the visibility timeout as a set that can never
        // complete, so the queue has to be genuinely empty afterwards.
        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<AmazonSqsTransport>();
        var queue = transport.Queues["oversized_3926"];

        var remaining = -1;
        for (var i = 0; i < 20; i++)
        {
            var attributes = await transport.Client!.GetQueueAttributesAsync(queue.QueueUrl,
                ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
                TestContext.Current.CancellationToken);

            remaining = attributes.ApproximateNumberOfMessages + attributes.ApproximateNumberOfMessagesNotVisible;
            if (remaining == 0) return;

            await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        }

        remaining.ShouldBe(0);
    }
}

public record BigMessage(Guid Id, string Contents);

public static class BigMessageHandler
{
    public static void Handle(BigMessage message)
    {
        // nothing
    }
}
