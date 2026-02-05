using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System.Linq;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.RateLimiting;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using CoreTests.Runtime;
using Xunit;

namespace CoreTests.RateLimiting;

public class rate_limiting_core_tests
{
    [Fact]
    public void rate_limit_window_matches_normal_and_wrapped_ranges()
    {
        var normal = new RateLimitWindow(new TimeOnly(8, 0), new TimeOnly(17, 0), RateLimit.PerMinute(10));
        normal.Matches(new TimeOnly(9, 0)).ShouldBeTrue();
        normal.Matches(new TimeOnly(18, 0)).ShouldBeFalse();

        var wrapped = new RateLimitWindow(new TimeOnly(22, 0), new TimeOnly(2, 0), RateLimit.PerMinute(10));
        wrapped.Matches(new TimeOnly(23, 0)).ShouldBeTrue();
        wrapped.Matches(new TimeOnly(1, 0)).ShouldBeTrue();
        wrapped.Matches(new TimeOnly(12, 0)).ShouldBeFalse();
    }

    [Fact]
    public void rate_limit_window_start_equals_end_matches_all_times()
    {
        var window = new RateLimitWindow(new TimeOnly(0, 0), new TimeOnly(0, 0), RateLimit.PerMinute(1));
        window.Matches(new TimeOnly(0, 0)).ShouldBeTrue();
        window.Matches(new TimeOnly(12, 0)).ShouldBeTrue();
    }

    [Fact]
    public void schedule_throws_on_invalid_default_limit()
    {
        var schedule = new RateLimitSchedule(new RateLimit(0, 1.Minutes()));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            schedule.Resolve(new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void schedule_throws_on_invalid_window_limit()
    {
        var schedule = new RateLimitSchedule(new RateLimit(1, 1.Minutes()))
        {
            TimeZone = TimeZoneInfo.Utc
        };
        schedule.AddWindow(new TimeOnly(8, 0), new TimeOnly(9, 0), new RateLimit(0, 1.Minutes()));

        Should.Throw<ArgumentOutOfRangeException>(() =>
            schedule.Resolve(new DateTimeOffset(2024, 1, 1, 8, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void settings_registry_matches_assignable_message_type()
    {
        var registry = new RateLimitSettingsRegistry();
        var schedule = new RateLimitSchedule(RateLimit.PerMinute(5));
        registry.RegisterMessageType(typeof(IBaseMessage), new RateLimitSettings("base", schedule));

        registry.TryFindForMessageType(typeof(DerivedMessage), out var settings).ShouldBeTrue();
        settings.Key.ShouldBe("base");
    }

    [Fact]
    public void settings_registry_matches_endpoint()
    {
        var registry = new RateLimitSettingsRegistry();
        var endpoint = new Uri("local://rate-limited");
        var schedule = new RateLimitSchedule(RateLimit.PerMinute(5));
        registry.RegisterEndpoint(endpoint, new RateLimitSettings("endpoint", schedule));

        registry.TryFindForEndpoint(endpoint, out var settings).ShouldBeTrue();
        settings.Key.ShouldBe("endpoint");
    }

    [Fact]
    public async Task in_memory_store_enforces_limit_per_window()
    {
        var store = new InMemoryRateLimitStore();
        var limit = new RateLimit(2, 1.Minutes());
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bucket = RateLimitBucket.For(limit, now);

        (await store.TryAcquireAsync(new RateLimitStoreRequest("key", bucket, 1, now), CancellationToken.None))
            .Allowed.ShouldBeTrue();
        (await store.TryAcquireAsync(new RateLimitStoreRequest("key", bucket, 1, now), CancellationToken.None))
            .Allowed.ShouldBeTrue();
        (await store.TryAcquireAsync(new RateLimitStoreRequest("key", bucket, 1, now), CancellationToken.None))
            .Allowed.ShouldBeFalse();

        var later = now.AddMinutes(1).AddSeconds(1);
        var nextBucket = RateLimitBucket.For(limit, later);
        (await store.TryAcquireAsync(new RateLimitStoreRequest("key", nextBucket, 1, later), CancellationToken.None))
            .Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task rate_limiter_prefers_listener_endpoint_over_message_type()
    {
        var options = new WolverineOptions();
        options.Policies.ForMessagesOfType<DerivedMessage>()
            .RateLimit("message", RateLimit.PerMinute(1));

        var listenerUri = new Uri("stub://listener");
        options.RateLimitEndpoint(listenerUri, RateLimit.PerMinute(1), key: "endpoint");

        var store = new CapturingRateLimitStore();
        var limiter = new RateLimiter(store, options, NullLogger<RateLimiter>.Instance);

        var envelope = new Envelope(new DerivedMessage())
        {
            Listener = new FakeListener(listenerUri)
        };

        await limiter.CheckAsync(envelope, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        store.LastRequest!.Key.ShouldBe("endpoint");
    }

    [Fact]
    public async Task rate_limiter_uses_destination_when_listener_missing()
    {
        var options = new WolverineOptions();
        options.Policies.ForMessagesOfType<DerivedMessage>()
            .RateLimit("message", RateLimit.PerMinute(1));

        var destination = new Uri("stub://destination");
        options.RateLimitEndpoint(destination, RateLimit.PerMinute(1), key: "endpoint");

        var store = new CapturingRateLimitStore();
        var limiter = new RateLimiter(store, options, NullLogger<RateLimiter>.Instance);

        var envelope = new Envelope(new DerivedMessage())
        {
            Destination = destination
        };

        await limiter.CheckAsync(envelope, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        store.LastRequest!.Key.ShouldBe("endpoint");
    }

    [Fact]
    public async Task middleware_throws_rate_limit_exception_with_expected_data()
    {
        var options = new WolverineOptions();
        options.Policies.ForMessagesOfType<DerivedMessage>()
            .RateLimit("key", RateLimit.PerMinute(1));

        var limiter = new RateLimiter(new DenyRateLimitStore(), options, NullLogger<RateLimiter>.Instance);
        var middleware = new RateLimitMiddleware(limiter);

        var envelope = new Envelope(new DerivedMessage());
        var ex = await Should.ThrowAsync<RateLimitExceededException>(() =>
            middleware.BeforeAsync(envelope, CancellationToken.None));

        ex.Key.ShouldBe("key");
        ex.Limit.Permits.ShouldBe(1);
        ex.RetryAfter.ShouldBeGreaterThan(TimeSpan.Zero);
        ex.RetryAfter.ShouldBeLessThanOrEqualTo(1.Minutes());
    }

    [Fact]
    public async Task continuation_reschedules_and_pauses_listener()
    {
        var runtime = new MockWolverineRuntime();
        var listenerUri = new Uri("stub://listener");
        var listener = new FakeListener(listenerUri);
        var agent = Substitute.For<IListeningAgent>();
        agent.Endpoint.Returns(new LocalQueue("rate-limited"));
        agent.PauseAsync(Arg.Any<TimeSpan>()).Returns(ValueTask.CompletedTask);

        runtime.Endpoints.FindListeningAgent(listenerUri).Returns(agent);

        var envelope = new Envelope(new DerivedMessage())
        {
            Listener = listener
        };

        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns(envelope);
        lifecycle.ReScheduleAsync(Arg.Any<DateTimeOffset>()).Returns(Task.CompletedTask);

        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var pause = 5.Seconds();

        var continuation = new RateLimitContinuation(pause);
        await continuation.ExecuteAsync(lifecycle, runtime, now, null);

        await lifecycle.Received(1).ReScheduleAsync(now.Add(pause));
        await agent.Received(1).PauseAsync(pause);
    }

    [Fact]
    public void continuation_source_describes_and_builds_from_exception()
    {
        var source = new RateLimitContinuationSource();
        source.Description.ShouldContain("Rate limit");

        var envelope = new Envelope(new DerivedMessage());
        var exception = new RateLimitExceededException("key", RateLimit.PerMinute(1), 10.Seconds());
        var continuation = source.Build(exception, envelope).ShouldBeOfType<RateLimitContinuation>();
        continuation.PauseTime.ShouldBe(10.Seconds());
    }

    [Fact]
    public void options_registers_rate_limiting_services_once()
    {
        var options = new WolverineOptions();
        options.Policies.ForMessagesOfType<DerivedMessage>()
            .RateLimit(RateLimit.PerMinute(1));

        options.RateLimitEndpoint(new Uri("local://rate-limited"), RateLimit.PerMinute(1));

        options.Services.Count(x => x.ServiceType == typeof(IRateLimitStore)).ShouldBe(1);
        options.Services.Count(x => x.ServiceType == typeof(RateLimiter)).ShouldBe(1);
    }

    private interface IBaseMessage;
    private sealed record DerivedMessage : IBaseMessage;

    private sealed class CapturingRateLimitStore : IRateLimitStore
    {
        public RateLimitStoreRequest? LastRequest { get; private set; }

        public ValueTask<RateLimitStoreResult> TryAcquireAsync(RateLimitStoreRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return new ValueTask<RateLimitStoreResult>(new RateLimitStoreResult(true, 1));
        }
    }

    private sealed class DenyRateLimitStore : IRateLimitStore
    {
        public ValueTask<RateLimitStoreResult> TryAcquireAsync(RateLimitStoreRequest request,
            CancellationToken cancellationToken)
        {
            return new ValueTask<RateLimitStoreResult>(new RateLimitStoreResult(false, request.Bucket.Limit + 1));
        }
    }

    private sealed class FakeListener : IListener
    {
        public FakeListener(Uri address)
        {
            Address = address;
        }

        public Uri Address { get; }
        public IHandlerPipeline? Pipeline => null;
        public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask StopAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
