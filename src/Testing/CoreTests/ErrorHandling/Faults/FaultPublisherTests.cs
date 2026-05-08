using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization.Encryption;
using Xunit;

namespace CoreTests.ErrorHandling.Faults;

public class FaultPublisherTests
{
    private record Foo(string Name);

    private record struct ValueMessage(int N);

    private static (FaultPublisher publisher, FaultPublishingPolicy policy, IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime)
        CreatePublisher()
    {
        var policy = new FaultPublishingPolicy();
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();

        // Default: routing returns one envelope so the existing tests keep their behavior.
        // The new no-route test overrides this on its own substitute.
        var runtime = Substitute.For<IWolverineRuntime>();
        var router = Substitute.For<Wolverine.Runtime.Routing.IMessageRouter>();
        runtime.RoutingFor(Arg.Any<Type>()).Returns(router);
        router
            .RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Returns(new[] { new Envelope() });

        var meter = new System.Diagnostics.Metrics.Meter("FaultPublisherTests");
        var publisher = new FaultPublisher(policy, runtime, NullLogger<FaultPublisher>.Instance, meter);
        return (publisher, policy, lifecycle, runtime);
    }

    private static Envelope EnvelopeFor(object message)
    {
        var env = new Envelope
        {
            Message = message,
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid(),
            Source = "tests",
            Attempts = 3,
            TenantId = "tenant-x",
        };
        env.Headers["x-custom"] = "v";
        return env;
    }

    [Fact]
    public async Task no_op_when_mode_is_none()
    {
        var (publisher, _, lifecycle, _) = CreatePublisher();
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, activity: null);

        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task publishes_fault_with_auto_header_when_dlq_only_and_trigger_is_dlq()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        await publisher.PublishIfEnabledAsync(lifecycle, new InvalidOperationException("boom"),
            FaultTrigger.MovedToErrorQueue, activity: null);

        await lifecycle.Received(1).PublishAsync(
            Arg.Is<Fault<Foo>>(f => f.Message.Name == "a" && f.Exception.Message == "boom"),
            Arg.Is<DeliveryOptions>(o => o.Headers[FaultHeaders.AutoPublished] == "true"));
    }

    [Fact]
    public async Task no_op_on_discard_when_dlq_only()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.Discarded, activity: null);

        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task publishes_on_discard_when_dlq_and_discard()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqAndDiscard);
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception("oops"), FaultTrigger.Discarded, activity: null);

        await lifecycle.Received(1).PublishAsync(
            Arg.Any<Fault<Foo>>(),
            Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task publish_failure_is_swallowed_and_does_not_throw()
    {
        var policy = new FaultPublishingPolicy();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var logger = Substitute.For<ILogger<FaultPublisher>>();
        var meter = new System.Diagnostics.Metrics.Meter("FaultPublisherTests");

        var runtime = Substitute.For<IWolverineRuntime>();
        var router = Substitute.For<Wolverine.Runtime.Routing.IMessageRouter>();
        runtime.RoutingFor(Arg.Any<Type>()).Returns(router);
        router
            .RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Returns(new[] { new Envelope() });

        var publisher = new FaultPublisher(policy, runtime, logger, meter);

        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));
        lifecycle
            .When(x => x.PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>()))
            .Do(_ => throw new InvalidOperationException("transport down"));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, activity: null);

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task no_op_when_envelope_message_is_null()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.GlobalMode = FaultPublishingMode.DlqOnly;
        var env = EnvelopeFor(new Foo("a"));
        env.Message = null;
        lifecycle.Envelope.Returns(env);

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, activity: null);

        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task fault_carries_envelope_metadata()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);
        var env = EnvelopeFor(new Foo("a"));
        lifecycle.Envelope.Returns(env);

        Fault<Foo>? captured = null;
        lifecycle
            .When(x => x.PublishAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>()))
            .Do(call => captured = call.Arg<Fault<Foo>>());

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception("kaput"), FaultTrigger.MovedToErrorQueue, activity: null);

        captured.ShouldNotBeNull();
        captured!.Attempts.ShouldBe(3);
        captured.CorrelationId.ShouldBe(env.CorrelationId);
        captured.ConversationId.ShouldBe(env.ConversationId);
        captured.TenantId.ShouldBe("tenant-x");
        captured.Source.ShouldBe("tests");
        captured.Headers["x-custom"].ShouldBe("v");
    }

    [Fact]
    public async Task no_op_when_message_type_is_value_type()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.GlobalMode = FaultPublishingMode.DlqOnly;

        var env = new Envelope
        {
            Message = new ValueMessage(42),
            Id = Guid.NewGuid(),
        };
        lifecycle.Envelope.Returns(env);

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, activity: null);

        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
        // No exceptions thrown, no log entries added — silent no-op.
    }

    [Fact]
    public async Task fault_preserves_headers_with_null_values()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);
        var env = new Envelope
        {
            Message = new Foo("a"),
            Id = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            Source = "tests",
            Attempts = 1,
        };
        env.Headers["nullable-header"] = null;
        lifecycle.Envelope.Returns(env);

        Fault<Foo>? captured = null;
        lifecycle
            .When(x => x.PublishAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>()))
            .Do(call => captured = call.Arg<Fault<Foo>>());

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, activity: null);

        captured.ShouldNotBeNull();
        captured!.Headers.ContainsKey("nullable-header").ShouldBeTrue();
        captured.Headers["nullable-header"].ShouldBeNull();
    }

    [Fact]
    public async Task auto_published_fault_strips_wolverine_encryption_namespace_headers()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);

        var env = EnvelopeFor(new Foo("a"));
        env.Headers[EncryptionHeaders.KeyIdHeader] = "k1";
        env.Headers[EncryptionHeaders.InnerContentTypeHeader] = "application/json";
        // Non-encryption wolverine.* key — must survive (proves the prefix isn't
        // over-broad, e.g. that nobody widened it to just "wolverine.").
        env.Headers["wolverine.something-else"] = "kept";
        lifecycle.Envelope.Returns(env);

        Fault<Foo>? captured = null;
        lifecycle
            .When(x => x.PublishAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>()))
            .Do(call => captured = call.Arg<Fault<Foo>>());

        await publisher.PublishIfEnabledAsync(
            lifecycle, new Exception("boom"), FaultTrigger.MovedToErrorQueue, activity: null);

        captured.ShouldNotBeNull();
        captured!.Headers["x-custom"].ShouldBe("v");
        captured.Headers["wolverine.something-else"].ShouldBe("kept");
        captured.Headers.Keys
            .Where(k => k.StartsWith(EncryptionHeaders.HeaderPrefix, StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task no_op_when_message_is_already_a_fault()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.GlobalMode = FaultPublishingMode.DlqOnly;

        var faultMessage = new Fault<Foo>(
            new Foo("inner"),
            ExceptionInfo.From(new InvalidOperationException("inner")),
            Attempts: 1,
            FailedAt: DateTimeOffset.UtcNow,
            CorrelationId: null,
            ConversationId: Guid.NewGuid(),
            TenantId: null,
            Source: null,
            Headers: new Dictionary<string, string?>());

        var env = new Envelope
        {
            Message = faultMessage,
            Id = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            Source = "tests",
            Attempts = 1,
        };
        lifecycle.Envelope.Returns(env);

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception("subscriber blew up"),
            FaultTrigger.MovedToErrorQueue, activity: null);

        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task records_fault_published_event_on_activity_on_success()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        using var activitySource = new ActivitySource("test-source");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("test")!;

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, activity);

        activity.Events.ShouldContain(e => e.Name == WolverineTracing.FaultPublished);
    }

    [Fact]
    public async Task records_fault_publish_failed_event_on_activity_on_failure()
    {
        var policy = new FaultPublishingPolicy { GlobalMode = FaultPublishingMode.DlqOnly };
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var meter = new System.Diagnostics.Metrics.Meter("FaultPublisherTests");

        var runtime = Substitute.For<IWolverineRuntime>();
        var router = Substitute.For<Wolverine.Runtime.Routing.IMessageRouter>();
        runtime.RoutingFor(Arg.Any<Type>()).Returns(router);
        router
            .RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Returns(new[] { new Envelope() });

        var publisher = new FaultPublisher(policy, runtime, NullLogger<FaultPublisher>.Instance, meter);

        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));
        lifecycle
            .When(x => x.PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>()))
            .Do(_ => throw new InvalidOperationException("transport down"));

        using var activitySource = new ActivitySource("test-source");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("test")!;

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, activity);

        activity.Events.ShouldContain(e => e.Name == WolverineTracing.FaultPublishFailed);
        activity.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task records_fault_no_route_event_when_no_routes_configured()
    {
        var (publisher, policy, lifecycle, runtime) = CreatePublisher();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        // Override the default routing substitute: no routes configured for Fault<Foo>.
        var emptyRouter = Substitute.For<Wolverine.Runtime.Routing.IMessageRouter>();
        emptyRouter
            .RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Returns(Array.Empty<Envelope>());
        runtime.RoutingFor(typeof(Fault<Foo>)).Returns(emptyRouter);

        using var activitySource = new ActivitySource("test-source");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("test")!;

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, activity);

        activity.Events.ShouldContain(e => e.Name == WolverineTracing.FaultNoRoute);
        activity.Events.ShouldNotContain(e => e.Name == WolverineTracing.FaultPublished);

        // The lifecycle's PublishAsync must NOT be called when no routes exist —
        // the pre-check short-circuits before delegating to MessageBus.
        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task counter_increments_when_fault_publish_fails()
    {
        var policy = new FaultPublishingPolicy { GlobalMode = FaultPublishingMode.DlqOnly };
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();

        // Use a unique meter name so a parallel run of records_fault_publish_failed_event_on_activity_on_failure
        // doesn't share an instrument with this test.
        var meter = new System.Diagnostics.Metrics.Meter("FaultPublisherTests-counter");

        var runtime = Substitute.For<IWolverineRuntime>();
        var router = Substitute.For<Wolverine.Runtime.Routing.IMessageRouter>();
        runtime.RoutingFor(Arg.Any<Type>()).Returns(router);
        router
            .RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Returns(new[] { new Envelope() });

        int observed = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter && instrument.Name == MetricsConstants.FaultPublishFailures)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((_, m, _, _) => Interlocked.Add(ref observed, m));
        listener.Start();

        var publisher = new FaultPublisher(policy, runtime, NullLogger<FaultPublisher>.Instance, meter);

        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));
        lifecycle
            .When(x => x.PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>()))
            .Do(_ => throw new InvalidOperationException("transport down"));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, activity: null);

        observed.ShouldBe(1);
    }

    [Fact]
    public async Task resolves_redaction_from_policy_and_passes_flags_to_ExceptionInfo()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.SetOverride(
            typeof(Foo),
            FaultPublishingMode.DlqOnly,
            includeExceptionMessage: false,
            includeStackTrace: false);
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        Fault<Foo>? captured = null;
        lifecycle
            .When(x => x.PublishAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>()))
            .Do(call => captured = call.Arg<Fault<Foo>>());

        Exception thrown;
        try { throw new InvalidOperationException("secret-canary-001"); }
        catch (Exception ex) { thrown = ex; }

        await publisher.PublishIfEnabledAsync(
            lifecycle, thrown, FaultTrigger.MovedToErrorQueue, activity: null);

        captured.ShouldNotBeNull();
        captured!.Exception.Type.ShouldBe(typeof(InvalidOperationException).FullName);
        captured.Exception.Message.ShouldBe(string.Empty);
        captured.Exception.StackTrace.ShouldBeNull();
    }

    [Fact]
    public async Task records_fault_recursion_suppressed_event_when_publishing_fault_of_fault()
    {
        var (publisher, policy, lifecycle, _) = CreatePublisher();
        policy.GlobalMode = FaultPublishingMode.DlqOnly;

        // Inbound envelope's message is itself a Fault<Foo> — the recursion guard must fire.
        var faultMessage = new Fault<Foo>(
            Message: new Foo("inner"),
            Exception: ExceptionInfo.From(new Exception("prior")),
            Attempts: 1,
            FailedAt: DateTimeOffset.UtcNow,
            CorrelationId: null,
            ConversationId: Guid.Empty,
            TenantId: null,
            Source: null,
            Headers: new Dictionary<string, string?>());
        var env = new Envelope { Message = faultMessage, Id = Guid.NewGuid() };
        lifecycle.Envelope.Returns(env);

        using var activitySource = new ActivitySource("test-source");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("test")!;

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, activity);

        activity.Events.ShouldContain(e => e.Name == WolverineTracing.FaultRecursionSuppressed);
        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task fault_no_route_event_includes_message_type_tag()
    {
        var policy = new FaultPublishingPolicy();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var meter = new System.Diagnostics.Metrics.Meter("FaultPublisherTests");

        var runtime = Substitute.For<IWolverineRuntime>();
        var router = Substitute.For<Wolverine.Runtime.Routing.IMessageRouter>();
        runtime.RoutingFor(Arg.Any<Type>()).Returns(router);
        router
            .RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Returns(Array.Empty<Envelope>()); // forces the no-route path

        var publisher = new FaultPublisher(policy, runtime, NullLogger<FaultPublisher>.Instance, meter);
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        using var activitySource = new ActivitySource("test-source");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("test")!;

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception("boom"),
            FaultTrigger.MovedToErrorQueue, activity);

        var noRouteEvent = activity.Events.FirstOrDefault(e => e.Name == WolverineTracing.FaultNoRoute);
        noRouteEvent.Name.ShouldBe(WolverineTracing.FaultNoRoute);
        noRouteEvent.Tags.ShouldContain(t =>
            t.Key == WolverineTracing.MessageType && (string?)t.Value == typeof(Foo).FullName);
    }

    [Fact]
    public async Task null_route_collection_does_not_throw()
    {
        var policy = new FaultPublishingPolicy();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var meter = new System.Diagnostics.Metrics.Meter("FaultPublisherTests");

        var runtime = Substitute.For<IWolverineRuntime>();
        var router = Substitute.For<Wolverine.Runtime.Routing.IMessageRouter>();
        runtime.RoutingFor(Arg.Any<Type>()).Returns(router);
        router
            .RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Returns((Envelope[]?)null);

        var publisher = new FaultPublisher(policy, runtime, NullLogger<FaultPublisher>.Instance, meter);
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception("boom"),
            FaultTrigger.MovedToErrorQueue, activity: null);

        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }
}
