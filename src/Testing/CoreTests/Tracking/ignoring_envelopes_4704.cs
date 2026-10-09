using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

/// <summary>
/// GH-4704. A tracked session records every envelope on the host during its window and only completes once
/// all of them finish, and until now the only scoping available was by message type. That does not help on a
/// shared host where the same message type is legitimately in flight for more than one reason at once --
/// another test's tail, or a background subscription running alongside the flow under test. IgnoreEnvelopes
/// filters on the whole envelope instead, so the caller can scope on destination, tenant, headers or
/// anything else they stamped.
/// </summary>
public class ignoring_envelopes_4704 : IDisposable
{
    private readonly IHost _host;
    private readonly TrackedSession theSession;

    public ignoring_envelopes_4704()
    {
        _host = WolverineHost.Basic();
        theSession = new TrackedSession(_host);
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    private Envelope record(object message, Action<Envelope>? configure = null)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Message = message;
        configure?.Invoke(envelope);

        theSession.Record(MessageEventType.Sent, envelope, "service", Guid.NewGuid());

        return envelope;
    }

    [Fact]
    public void an_envelope_matching_the_filter_is_not_recorded()
    {
        theSession.IgnoreEnvelopes(e => e.TenantId == "other");

        record(new TrackedThing(), e => e.TenantId = "other");

        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void an_envelope_not_matching_the_filter_is_still_recorded()
    {
        theSession.IgnoreEnvelopes(e => e.TenantId == "other");

        record(new TrackedThing(), e => e.TenantId = "mine");

        theSession.AllRecordsInOrder().Length.ShouldBe(1);
    }

    [Fact]
    public void the_same_message_type_can_be_scoped_two_ways_at_once()
    {
        // The actual point of the feature. IgnoreMessagesMatchingType cannot express this: both envelopes
        // carry the same message type and only one of them belongs to the flow under test.
        theSession.IgnoreEnvelopes(e => e.TenantId == "background");

        record(new TrackedThing(), e => e.TenantId = "background");
        record(new TrackedThing(), e => e.TenantId = "under-test");

        var records = theSession.AllRecordsInOrder();
        records.Length.ShouldBe(1);
        records[0].Envelope!.TenantId.ShouldBe("under-test");
    }

    [Fact]
    public void filters_compose_so_any_match_ignores()
    {
        theSession.IgnoreEnvelopes(e => e.TenantId == "one");
        theSession.IgnoreEnvelopes(e => e.TenantId == "two");

        record(new TrackedThing(), e => e.TenantId = "one");
        record(new TrackedThing(), e => e.TenantId = "two");
        record(new TrackedThing(), e => e.TenantId = "three");

        var records = theSession.AllRecordsInOrder();
        records.Length.ShouldBe(1);
        records[0].Envelope!.TenantId.ShouldBe("three");
    }

    [Fact]
    public void message_type_rules_still_apply_alongside_envelope_rules()
    {
        theSession.IgnoreMessageTypes(t => t == typeof(OtherTrackedThing));
        theSession.IgnoreEnvelopes(e => e.TenantId == "background");

        record(new OtherTrackedThing(), e => e.TenantId = "under-test");
        record(new TrackedThing(), e => e.TenantId = "background");
        record(new TrackedThing(), e => e.TenantId = "under-test");

        theSession.AllRecordsInOrder().Length.ShouldBe(1);
    }

    [Fact]
    public void acknowledgements_are_never_ignored_however_broad_the_filter()
    {
        // Load-bearing, and the reason the acknowledgement check runs before the envelope rules. The
        // session's own acknowledgement APIs -- SendMessageAndWaitForAcknowledgementAsync,
        // AssertAnyFailureAcknowledgements -- depend on these being recorded, and an envelope predicate is
        // far likelier to sweep one up by accident than a message-type predicate is. Mirrors the carve-out
        // isIgnoredMessageType already makes.
        theSession.IgnoreEnvelopes(_ => true);

        record(new Acknowledgement());
        record(new FailureAcknowledgement());

        theSession.AllRecordsInOrder().Length.ShouldBe(2);
    }

    [Fact]
    public void the_filter_applies_to_the_maybe_record_path_too()
    {
        // Record and MaybeRecord are separate entry points and both had their own ignore check. Missing one
        // would leave the filter half-applied depending on which event type arrived.
        theSession.IgnoreEnvelopes(e => e.TenantId == "other");

        var envelope = ObjectMother.Envelope();
        envelope.Message = new TrackedThing();
        envelope.TenantId = "other";

        theSession.MaybeRecord(MessageEventType.Sent, envelope, "service", Guid.NewGuid());

        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void no_filter_means_nothing_extra_is_ignored()
    {
        // The negative control: the feature is opt-in and must not change a session that never calls it.
        record(new TrackedThing(), e => e.TenantId = "anything");

        theSession.AllRecordsInOrder().Length.ShouldBe(1);
    }
}

/// <summary>
/// The wiring half: TrackedSessionConfiguration.IgnoreEnvelopes has to actually reach the session. The unit
/// facts above drive TrackedSession directly, so they would all pass against a configuration method that
/// did nothing.
/// </summary>
public class ignoring_envelopes_through_the_configuration_4704
{
    [Fact]
    public async Task the_configuration_method_reaches_the_session()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TrackedThingHandler));
                opts.PublishMessage<TrackedThing>().ToLocalQueue("under-test");
                opts.PublishMessage<OtherTrackedThing>().ToLocalQueue("background");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var ignored = new Uri("local://background");

        var session = await host.TrackActivity()
            .IgnoreEnvelopes(e => e.Destination == ignored)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async bus =>
            {
                await bus.PublishAsync(new TrackedThing());
                await bus.PublishAsync(new OtherTrackedThing());
            }));

        session.AllRecordsInOrder().ShouldNotBeEmpty();
        session.AllRecordsInOrder().ShouldAllBe(x => x.Envelope!.Destination != ignored);
        session.Executed.MessagesOf<OtherTrackedThing>().ShouldBeEmpty();
        session.Executed.MessagesOf<TrackedThing>().ShouldHaveSingleItem();
    }

    /// <summary>
    /// A stage added with AddStage runs as a child TrackedSession built by the copy constructor in
    /// TrackedSession.Execution.cs, and the child's records are merged back into the parent. The filter has to
    /// travel with the other ignore rules, or everything recorded during the stage passes it.
    /// </summary>
    [Fact]
    public async Task the_filter_also_applies_inside_a_secondary_stage()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TrackedThingHandler));
                opts.PublishMessage<TrackedThing>().ToLocalQueue("under-test");
                opts.PublishMessage<OtherTrackedThing>().ToLocalQueue("background");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var ignored = new Uri("local://background");

        var session = await host.TrackActivity()
            .IgnoreEnvelopes(e => e.Destination == ignored)
            .AddStage(async (_, bus, _) =>
            {
                await bus.PublishAsync(new TrackedThing());
                await bus.PublishAsync(new OtherTrackedThing());
            })
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => Task.CompletedTask));

        session.AllRecordsInOrder().ShouldAllBe(x => x.Envelope!.Destination != ignored);
        session.Executed.MessagesOf<OtherTrackedThing>().ShouldBeEmpty();
        session.Executed.MessagesOf<TrackedThing>().ShouldHaveSingleItem();
    }
}

public record TrackedThing;

public record OtherTrackedThing;

public static class TrackedThingHandler
{
    public static void Handle(TrackedThing message)
    {
    }

    public static void Handle(OtherTrackedThing message)
    {
    }
}
