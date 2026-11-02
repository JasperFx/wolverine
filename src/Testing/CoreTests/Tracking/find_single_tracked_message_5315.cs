using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

/// <summary>
/// GH-5315. FindSingleTrackedMessageOfType used to apply .Distinct() to the tracked messages, so a handler
/// that published two value-equal records passed a test asserting that exactly one was published.
/// The messages are now counted by envelope: one publish counts once however many records it has, a
/// double publish counts twice, and one message routed to several destinations still counts once.
/// </summary>
public class find_single_tracked_message_5315 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Publishing5315Handler))
                    .IncludeType(typeof(Published5315Handler))
                    .IncludeType(typeof(FannedOut5315Handler));

                opts.PublishMessage<FannedOut5315>().ToLocalQueue("fan-out-one");
                opts.PublishMessage<FannedOut5315>().ToLocalQueue("fan-out-two");
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_single_publish_is_found()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new PublishEqualRecords5315(1));

        tracked.FindSingleTrackedMessageOfType<Published5315>().Name.ShouldBe("same");
        tracked.FindSingleTrackedMessageOfType<Published5315>(MessageEventType.MessageSucceeded)
            .Name.ShouldBe("same");
        tracked.Sent.SingleMessage<Published5315>().Name.ShouldBe("same");
    }

    [Fact]
    public async Task a_double_publish_of_an_equal_record_throws()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new PublishEqualRecords5315(2));

        Should.Throw<InvalidOperationException>(() => tracked.FindSingleTrackedMessageOfType<Published5315>())
            .Message.ShouldContain("detected 2");
    }

    [Fact]
    public async Task a_double_publish_of_an_equal_record_throws_for_one_event_type()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new PublishEqualRecords5315(2));

        Should.Throw<InvalidOperationException>(() =>
                tracked.FindSingleTrackedMessageOfType<Published5315>(MessageEventType.MessageSucceeded))
            .Message.ShouldContain("detected 2");

        Should.Throw<InvalidOperationException>(() =>
            tracked.FindSingleTrackedMessageOfType<Published5315>(MessageEventType.Sent));
    }

    [Fact]
    public async Task one_message_routed_to_two_destinations_counts_once()
    {
        var tracked = await _host.TrackActivity()
            .ExecuteAndWaitAsync(c => c.PublishAsync(new FannedOut5315("fanned")));

        // Two envelopes, one to each local queue, carrying the same message instance
        tracked.Sent.Envelopes().Count(x => x.Message is FannedOut5315).ShouldBe(2);

        tracked.FindSingleTrackedMessageOfType<FannedOut5315>().Name.ShouldBe("fanned");
        tracked.FindSingleTrackedMessageOfType<FannedOut5315>(MessageEventType.MessageSucceeded)
            .Name.ShouldBe("fanned");
    }

    [Fact]
    public void received_copies_over_a_transport_join_the_publish_they_came_from()
    {
        // Simulates one publish fanned out to two external destinations: each received envelope has the
        // id of the envelope that was sent, but holds its own deserialized copy of the message
        var session = new TrackedSession(_host);
        var message = new Published5315("over-the-wire");

        foreach (var destination in new[] { "tcp://localhost:2001", "tcp://localhost:2002" })
        {
            var sent = ObjectMother.Envelope();
            sent.Message = message;
            sent.Destination = new Uri(destination);
            session.Record(MessageEventType.Sent, sent, "sender", Guid.NewGuid());

            var received = ObjectMother.Envelope();
            received.Id = sent.Id;
            received.Message = message with { };
            session.Record(MessageEventType.Received, received, "receiver", Guid.NewGuid());
            session.Record(MessageEventType.MessageSucceeded, received, "receiver", Guid.NewGuid());
        }

        session.FindSingleTrackedMessageOfType<Published5315>().Name.ShouldBe("over-the-wire");
        session.FindSingleTrackedMessageOfType<Published5315>(MessageEventType.MessageSucceeded)
            .Name.ShouldBe("over-the-wire");
    }
}

public record PublishEqualRecords5315(int Count);

public record Published5315(string Name);

public record FannedOut5315(string Name);

public static class Publishing5315Handler
{
    public static async Task Handle(PublishEqualRecords5315 command, IMessageBus bus)
    {
        for (var i = 0; i < command.Count; i++)
        {
            // A new instance each time, but value-equal to the last one
            await bus.PublishAsync(new Published5315("same"));
        }
    }
}

public static class Published5315Handler
{
    public static void Handle(Published5315 message)
    {
    }
}

public static class FannedOut5315Handler
{
    public static void Handle(FannedOut5315 message)
    {
    }
}
