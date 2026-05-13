using System.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// Coverage for the <see cref="Envelope"/> pooling work landed for
/// wolverine#2726. Two acceptance gates the issue called out by name:
///
///   - <see cref="Reset_zeroes_every_settable_property"/> reflects over the
///     public surface to catch drift when a new field is added without
///     being zeroed in <c>Envelope.Reset</c>.
///   - <see cref="Tracked_session_envelope_record_survives_after_handler_returns"/>
///     is the Q1-A acceptance test — proves the
///     <c>ActiveSession</c> gate in <see cref="WolverineRuntime.AcquireInternalEnvelope"/>
///     keeps tracking sessions on fresh allocations so envelope references
///     captured by <see cref="EnvelopeRecord"/> aren't corrupted by a recycle.
/// </summary>
public class envelope_pool_tests
{
    [Fact]
    public void Reset_zeroes_every_settable_property()
    {
        // Stamp every public settable property with a non-default value so
        // Reset() has something to clear. Skips a few properties whose
        // setters perform validation that doesn't accept arbitrary input
        // (e.g. DeliverWithin throws on null, Headers is read-only-with-
        // backing-init); their backing fields are reset via the
        // <c>_headers</c> / <c>_deliverWithin</c> branches in Reset itself
        // and asserted via the public-surface contracts below.
        var envelope = new Envelope
        {
            DeliverBy = DateTimeOffset.UtcNow,
            AckRequested = true,
            ScheduledTime = DateTimeOffset.UtcNow,
            Data = [1, 2, 3],
            Message = new SomeProbeMessage(),
            Attempts = 4,
            SendAttempts = 5,
            SentAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Source = "src",
            MessageType = "msg-type",
            ReplyUri = new Uri("local://reply"),
            ContentType = "application/json",
            CorrelationId = "corr",
            SagaId = "saga",
            ConversationId = Guid.NewGuid(),
            Destination = new Uri("local://dest"),
            ParentId = "parent",
            TenantId = "tenant",
            UserName = "user",
            AcceptedContentTypes = ["text/plain"],
            Id = Guid.NewGuid(),
            TopicName = "topic",
            EndpointName = "endpoint",
            WasPersistedInOutbox = true,
            GroupId = "group",
            DeduplicationId = "dedup",
            PartitionKey = "pkey",
            RoutingInformation = "anything",
            Offset = 123L,
            PartitionId = 7,
            KeepUntil = DateTimeOffset.UtcNow,
        };

        // Also stamp the Headers dictionary so Reset's _headers = null path
        // is exercised.
        envelope.Headers["k"] = "v";

        // Reset is internal — same assembly via InternalsVisibleTo.
        typeof(Envelope)
            .GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(envelope, null);

        // Reflection guard: every public settable property must be at its
        // type's default. Skips the few setter-validated properties whose
        // backing fields are asserted via separate public surfaces below.
        var setterValidated = new HashSet<string>
        {
            // setter throws on null
            nameof(Envelope.DeliverWithin),
            // setter side-effect-only (ScheduledTime); covered by ScheduledTime assertion
            nameof(Envelope.ScheduleDelay),
            // Headers has a get-or-init pattern via _headers backing field;
            // we assert Reset cleared _headers via the indexer-on-fresh
            // contract below.
            nameof(Envelope.Headers),
            // Data getter calls AssertMessage(), which throws when both
            // _data and _message are null — exactly the state Reset()
            // leaves the envelope in. We assert the private _data field
            // directly below.
            nameof(Envelope.Data),
        };

        foreach (var prop in typeof(Envelope).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;
            if (setterValidated.Contains(prop.Name)) continue;
            // Skip indexed properties (none today, but defensive)
            if (prop.GetIndexParameters().Length > 0) continue;

            var actual = prop.GetValue(envelope);
            var expected = DefaultFor(prop);
            Assert.True(
                EqualityComparer<object?>.Default.Equals(actual, expected),
                $"Envelope.{prop.Name} not reset: expected {expected ?? "null"}, got {actual ?? "null"}");
        }

        // _headers cleared — accessing Headers should return a brand new
        // empty dict, not the one we populated above.
        envelope.Headers.ShouldBeEmpty();
        envelope.Headers.ContainsKey("k").ShouldBeFalse();

        // _data cleared — assert via the backing field, since the Data
        // property getter throws when both _data and _message are null
        // (which is exactly the post-Reset state).
        var dataField = typeof(Envelope)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic);
        dataField.ShouldNotBeNull();
        dataField.GetValue(envelope).ShouldBeNull();
    }

    private static object? DefaultFor(PropertyInfo prop)
    {
        // AcceptedContentTypes resets to the static default array (a value
        // semantically equivalent to a freshly-constructed envelope), not
        // null. That keeps callers' span-over-default-content-types fast
        // path intact and matches the Envelope() constructor default.
        if (prop.Name == nameof(Envelope.AcceptedContentTypes))
            return Envelope.DefaultAcceptedContentTypes;

        return prop.PropertyType.IsValueType
            ? Activator.CreateInstance(prop.PropertyType)
            : null;
    }

    [Fact]
    public async Task Tracked_session_envelope_record_survives_after_handler_returns()
    {
        // Q1-A acceptance test from #2726. When a tracking session is active,
        // AcquireInternalEnvelope must allocate fresh (not pool) so an
        // envelope reference captured by EnvelopeRecord inside the handler
        // still holds the original values after the handler returns.
        // If we mistakenly pooled the envelope, Reset() would zero those
        // fields and the captured reference would observe corrupted state.

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "envelope-pool-tracking-test";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<EnvelopeCapturingHandler>();
            })
            .StartAsync();

        EnvelopeCapturingHandler.LastSeen = null;
        EnvelopeCapturingHandler.CapturedId = default;
        EnvelopeCapturingHandler.CapturedCorrelationId = null;

        var probe = new TrackedProbeMessage("probe-payload");
        await host.InvokeMessageAndWaitAsync(probe);

        var captured = EnvelopeCapturingHandler.LastSeen.ShouldNotBeNull();
        // After the handler returns and the tracking session completes,
        // the captured reference should still observe its original values.
        // Under pooling-when-tracking-is-active (the bug), Reset() would
        // have wiped these to default after the InvokeAsync finally block.
        captured.Id.ShouldBe(EnvelopeCapturingHandler.CapturedId);
        captured.Id.ShouldNotBe(Guid.Empty);
        captured.CorrelationId.ShouldBe(EnvelopeCapturingHandler.CapturedCorrelationId);
        captured.Message.ShouldBeOfType<TrackedProbeMessage>().Payload.ShouldBe("probe-payload");
    }

    public sealed record SomeProbeMessage;
    public sealed record TrackedProbeMessage(string Payload);

    public sealed class EnvelopeCapturingHandler
    {
        public static Envelope? LastSeen;
        public static Guid CapturedId;
        public static string? CapturedCorrelationId;

        public static void Handle(TrackedProbeMessage _, Envelope envelope)
        {
            LastSeen = envelope;
            CapturedId = envelope.Id;
            CapturedCorrelationId = envelope.CorrelationId;
        }
    }
}
