using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

/// <summary>
/// A host that embeds a tool with messaging of its own -- an embedded CritterWatch console polls and publishes
/// for the whole life of the host -- always has an envelope in flight, so a tracked session, which waits for every
/// envelope it saw to finish, never completes. The ignore rules written once on <c>opts.Tracking</c> apply to
/// every tracked session against the host, so the session (and every WolverineFx.Bobcat act built on one) waits
/// for the application's own messages only.
/// </summary>
/// <remarks>
/// The background traffic here is <see cref="TargetMessage1" />, a message type from another assembly
/// (Wolverine.ComplianceTests), so that ignoring "everything from that assembly" leaves this assembly's own
/// messages tracked.
/// </remarks>
public class ignoring_traffic_for_every_session_on_a_host
{
    private static readonly Uri BackgroundQueue = new("local://background");

    private static Task<IHost> startHost(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(TrackedThingHandler))
                    .IncludeType(typeof(BackgroundTrafficHandler));

                opts.PublishMessage<TargetMessage1>().ToLocalQueue("background");
                opts.PublishMessage<TrackedThing>().ToLocalQueue("under-test");
                opts.PublishMessage<OtherTrackedThing>().ToLocalQueue("under-test");

                configure(opts);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Publishes <see cref="TargetMessage1" /> continuously until disposed. Each one takes a while to handle, so
    /// some are always in flight -- the shape of an embedded console's polling.
    /// </summary>
    private sealed class BackgroundTraffic : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _loop;

        public BackgroundTraffic(IHost host, string color = "blue")
        {
            _loop = Task.Run(async () =>
            {
                var number = 0;
                while (!_cancellation.IsCancellationRequested)
                {
                    await host.MessageBus().PublishAsync(new TargetMessage1(Guid.NewGuid(), ++number, color));
                    try
                    {
                        await Task.Delay(10, _cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
#pragma warning disable VSTHRD003
            await _loop;
#pragma warning restore VSTHRD003
        }
    }

    [Fact]
    public async Task continuous_ignored_traffic_does_not_stop_a_tracked_session_from_completing()
    {
        using var host = await startHost(opts =>
            opts.Tracking.IgnoreMessagesFromAssembly(typeof(TargetMessage1).Assembly));

        await using var traffic = new BackgroundTraffic(host);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var session = await host.TrackActivity().Timeout(5.Seconds())
            .InvokeMessageAndWaitAsync(new TrackedThing());

        session.Status.ShouldBe(TrackingStatus.Completed);
        session.AllRecordsInOrder().ShouldNotContain(x => x.Envelope!.Message is TargetMessage1);
    }

    /// <summary>
    /// The control for the test above: the same traffic, with no rule, holds the session open until it times
    /// out. Without this, the test above could pass because the traffic never actually overlapped the session.
    /// </summary>
    [Fact]
    public async Task without_the_rule_the_same_traffic_holds_the_session_open()
    {
        using var host = await startHost(_ => { });

        await using var traffic = new BackgroundTraffic(host);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var session = await host.TrackActivity().Timeout(1.Seconds()).DoNotAssertOnTimeout()
            .InvokeMessageAndWaitAsync(new TrackedThing());

        session.Status.ShouldBe(TrackingStatus.TimedOut);
        session.AllRecordsInOrder().ShouldContain(x => x.Envelope!.Message is TargetMessage1);
    }

    [Fact]
    public async Task continuous_traffic_to_an_ignored_endpoint_does_not_stop_a_tracked_session_from_completing()
    {
        using var host = await startHost(opts => opts.Tracking.IgnoreEndpoint(BackgroundQueue));

        await using var traffic = new BackgroundTraffic(host);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var session = await host.TrackActivity().Timeout(5.Seconds())
            .InvokeMessageAndWaitAsync(new TrackedThing());

        session.Status.ShouldBe(TrackingStatus.Completed);
        session.AllRecordsInOrder().ShouldNotContain(x => x.Envelope!.Destination == BackgroundQueue);
    }

    [Fact]
    public async Task exceptions_thrown_by_ignored_traffic_do_not_fail_the_session()
    {
        using var host = await startHost(opts =>
            opts.Tracking.IgnoreMessagesFromAssembly(typeof(TargetMessage1).Assembly));

        await using var traffic = new BackgroundTraffic(host, BackgroundTrafficHandler.FailingColor);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // The session asserts no exceptions by default, so this throws if an ignored handler's failure is
        // attributed to it
        var session = await host.TrackActivity().Timeout(5.Seconds())
            .InvokeMessageAndWaitAsync(new TrackedThing());

        session.AllExceptions().ShouldBeEmpty();
        ((TrackedSession)session).AssertNoExceptionsWereThrown();
        session.MovedToErrorQueue.Envelopes().ShouldBeEmpty();
    }

    [Fact]
    public async Task non_ignored_traffic_still_tracks_normally()
    {
        using var host = await startHost(opts =>
            opts.Tracking.IgnoreMessagesFromAssembly(typeof(TargetMessage1).Assembly));

        await using var traffic = new BackgroundTraffic(host);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var session = await host.TrackActivity().Timeout(5.Seconds())
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async bus =>
            {
                await bus.PublishAsync(new TrackedThing());
                await bus.PublishAsync(new OtherTrackedThing());
            }));

        session.Sent.MessagesOf<TrackedThing>().ShouldHaveSingleItem();
        session.Executed.MessagesOf<TrackedThing>().ShouldHaveSingleItem();
        session.Executed.MessagesOf<OtherTrackedThing>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task a_failure_in_non_ignored_traffic_still_fails_the_session()
    {
        using var host = await startHost(opts =>
            opts.Tracking.IgnoreMessagesFromAssembly(typeof(TargetMessage1).Assembly));

        // The rule ignores TargetMessage1 only; an exception from this assembly's traffic is still the act's
        await Should.ThrowAsync<Exception>(() => host.TrackActivity().Timeout(5.Seconds())
            .InvokeMessageAndWaitAsync(new FailingTrackedThing()));
    }

    [Fact]
    public async Task a_session_can_include_the_traffic_the_host_ignores()
    {
        using var host = await startHost(opts =>
            opts.Tracking.IgnoreMessagesFromAssembly(typeof(TargetMessage1).Assembly));

        // Ignored, so the session does not wait for it: it completes without seeing it executed
        var ignoring = await host.TrackActivity().Timeout(5.Seconds())
            .PublishMessageAndWaitAsync(new TargetMessage1(Guid.NewGuid(), 1, "green"));
        ignoring.Executed.MessagesOf<TargetMessage1>().ShouldBeEmpty();

        // Included, so the session waits for it and records it. (The first message may finish inside this
        // session's window too, which is why this looks for the second by value.)
        var second = new TargetMessage1(Guid.NewGuid(), 2, "green");
        var including = await host.TrackActivity().Timeout(5.Seconds()).IncludeHostIgnoredTraffic()
            .PublishMessageAndWaitAsync(second);
        including.Executed.MessagesOf<TargetMessage1>().ShouldContain(second);
    }

    [Fact]
    public async Task a_session_can_ignore_an_assembly_on_its_own()
    {
        using var host = await startHost(_ => { });

        await using var traffic = new BackgroundTraffic(host);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var session = await host.TrackActivity().Timeout(5.Seconds())
            .IgnoreMessagesFromAssembly(typeof(TargetMessage1).Assembly)
            .InvokeMessageAndWaitAsync(new TrackedThing());

        session.Status.ShouldBe(TrackingStatus.Completed);
        session.AllRecordsInOrder().ShouldNotContain(x => x.Envelope!.Message is TargetMessage1);
    }

    [Fact]
    public async Task a_session_can_ignore_an_endpoint_on_its_own()
    {
        using var host = await startHost(_ => { });

        await using var traffic = new BackgroundTraffic(host);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var session = await host.TrackActivity().Timeout(5.Seconds())
            .IgnoreEndpoint(BackgroundQueue)
            .InvokeMessageAndWaitAsync(new TrackedThing());

        session.Status.ShouldBe(TrackingStatus.Completed);
        session.AllRecordsInOrder().ShouldNotContain(x => x.Envelope!.Destination == BackgroundQueue);
    }
}

/// <summary>
/// The rule-level facts, driven against <see cref="TrackedSession" /> directly.
/// </summary>
public class host_wide_tracking_ignore_rules : IDisposable
{
    private readonly IHost _host;
    private readonly TrackedSession theSession;

    public host_wide_tracking_ignore_rules()
    {
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Tracking.IgnoreMessagesFromAssembly(typeof(TargetMessage1).Assembly);
                opts.Tracking.IgnoreEndpoint(new Uri("local://ignored"));
            })
            .Start();
        theSession = new TrackedSession(_host);
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    private Envelope record(object message, Action<Envelope>? configure = null, Exception? ex = null)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Message = message;
        configure?.Invoke(envelope);
        theSession.Record(MessageEventType.Sent, envelope, "service", Guid.NewGuid(), ex);
        return envelope;
    }

    [Fact]
    public void a_message_from_an_ignored_assembly_is_not_recorded()
    {
        record(new TargetMessage1(Guid.NewGuid(), 1, "red"));
        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void a_batch_of_messages_from_an_ignored_assembly_is_not_recorded()
    {
        // Batched messages travel as an array of the message type
        record(new[] { new TargetMessage1(Guid.NewGuid(), 1, "red") });
        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void a_batch_of_system_commands_is_not_recorded()
    {
        record(new[] { new FakeBatchedSystemCommand() });
        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void traffic_to_an_ignored_endpoint_is_not_recorded_whatever_the_trailing_slash()
    {
        record(new TrackedThing(), e => e.Destination = new Uri("local://ignored/"));
        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void other_traffic_is_still_recorded()
    {
        record(new TrackedThing(), e => e.Destination = new Uri("local://elsewhere"));
        theSession.AllRecordsInOrder().Length.ShouldBe(1);
    }

    [Fact]
    public void an_exception_recorded_with_ignored_traffic_is_not_reported()
    {
        record(new TargetMessage1(Guid.NewGuid(), 1, "red"), ex: new DivideByZeroException());

        theSession.AllExceptions().ShouldBeEmpty();
        Should.NotThrow(() => theSession.AssertNoExceptionsWereThrown());
    }

    [Fact]
    public void an_exception_logged_by_the_id_of_ignored_traffic_is_not_reported()
    {
        // The pipeline's last-ditch recovery reports an exception by envelope id alone
        var ignored = record(new TargetMessage1(Guid.NewGuid(), 1, "red"));
        theSession.LogException(new DivideByZeroException(), "service", ignored.Id);

        theSession.AllExceptions().ShouldBeEmpty();
    }

    [Fact]
    public void an_exception_logged_by_the_id_of_other_traffic_is_still_reported()
    {
        var tracked = record(new TrackedThing());
        theSession.LogException(new DivideByZeroException(), "service", tracked.Id);

        theSession.AllExceptions().ShouldHaveSingleItem();
        Should.Throw<AggregateException>(() => theSession.AssertNoExceptionsWereThrown());
    }

    [Fact]
    public void a_session_that_includes_host_ignored_traffic_records_it()
    {
        theSession.IncludeHostIgnoredTraffic();

        record(new TargetMessage1(Guid.NewGuid(), 1, "red"));
        record(new TrackedThing(), e => e.Destination = new Uri("local://ignored"));

        theSession.AllRecordsInOrder().Length.ShouldBe(2);
    }

    [Fact]
    public void including_host_ignored_traffic_does_not_include_system_commands()
    {
        theSession.IncludeHostIgnoredTraffic();

        record(new FakeBatchedSystemCommand());

        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void a_host_with_no_rules_ignores_nothing_extra()
    {
        using var host = WolverineHost.Basic();
        var session = new TrackedSession(host);

        var envelope = ObjectMother.Envelope();
        envelope.Message = new TargetMessage1(Guid.NewGuid(), 1, "red");
        session.Record(MessageEventType.Sent, envelope, "service", Guid.NewGuid());

        session.AllRecordsInOrder().Length.ShouldBe(1);
    }

    public class FakeBatchedSystemCommand : ISystemCommand;
}

public record FailingTrackedThing;

public static class BackgroundTrafficHandler
{
    public const string FailingColor = "fail";

    public static async Task Handle(TargetMessage1 message)
    {
        // Slow enough that some are always in flight while the publisher runs every 10ms
        await Task.Delay(50);

        if (message.Color == FailingColor)
        {
            throw new DivideByZeroException("Background traffic failed, as it was told to");
        }
    }

    public static void Handle(FailingTrackedThing message)
    {
        throw new DivideByZeroException("The act's own message failed");
    }
}
