using JasperFx.Core;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.WorkerQueues;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-3710. Unit tests for the default in-memory idempotency guard. The rotation policy is tested against an
/// injected clock rather than Task.Delay -- the whole point of the generational scheme is that eviction is
/// decided by two cheap comparisons, so there is nothing here that needs real time to pass.
/// </summary>
public class generational_idempotency_guard
{
    private DateTimeOffset theTime = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private GenerationalIdempotencyGuard guardFor(TimeSpan? window = null, int? maxTracked = null,
        MessageIdentity identity = MessageIdentity.IdOnly)
    {
        var settings = new InMemoryIdempotencySettings();
        if (window.HasValue) settings.Window = window.Value;
        if (maxTracked.HasValue) settings.MaxTracked = maxTracked.Value;

        return new GenerationalIdempotencyGuard(settings, identity, () => theTime);
    }

    private static Envelope envelopeFor(Guid? id = null, string destination = "stub://one")
    {
        return new Envelope
        {
            Id = id ?? Guid.NewGuid(),
            Destination = new Uri(destination)
        };
    }

    [Fact]
    public void first_time_through_is_always_allowed()
    {
        var guard = guardFor();
        guard.TryBeginProcessing(envelopeFor()).ShouldBeTrue();
    }

    [Fact]
    public void a_duplicate_of_an_in_flight_message_is_rejected()
    {
        var guard = guardFor();
        var envelope = envelopeFor();

        guard.TryBeginProcessing(envelope).ShouldBeTrue();

        // The original has NOT completed. The duplicate still has to be turned away, or it would sit in the
        // execution block waiting to run the same message a second time.
        guard.TryBeginProcessing(envelopeFor(envelope.Id)).ShouldBeFalse();
    }

    [Fact]
    public void a_duplicate_of_a_processed_message_is_rejected()
    {
        var guard = guardFor();
        var envelope = envelopeFor();

        guard.TryBeginProcessing(envelope).ShouldBeTrue();
        guard.MarkProcessed(envelope);

        guard.TryBeginProcessing(envelopeFor(envelope.Id)).ShouldBeFalse();
        guard.InFlightCount.ShouldBe(0);
        guard.TrackedCount.ShouldBe(1);
    }

    [Fact]
    public void a_released_message_is_not_remembered_and_can_run_again()
    {
        var guard = guardFor();
        var envelope = envelopeFor();

        guard.TryBeginProcessing(envelope).ShouldBeTrue();

        // The failure path: nacked or requeued, so the broker is going to send it again and the retry must
        // be allowed to execute.
        guard.Release(envelope);

        guard.TrackedCount.ShouldBe(0);
        guard.InFlightCount.ShouldBe(0);
        guard.TryBeginProcessing(envelopeFor(envelope.Id)).ShouldBeTrue();
    }

    [Fact]
    public void releasing_after_a_previous_success_forgets_the_id_entirely()
    {
        var guard = guardFor();
        var envelope = envelopeFor();

        guard.TryBeginProcessing(envelope).ShouldBeTrue();
        guard.MarkProcessed(envelope);
        guard.Release(envelope);

        guard.TryBeginProcessing(envelopeFor(envelope.Id)).ShouldBeTrue();
    }

    [Fact]
    public void an_id_survives_the_first_generation_rotation()
    {
        var guard = guardFor(10.Minutes());
        var envelope = envelopeFor();

        guard.TryBeginProcessing(envelope).ShouldBeTrue();
        guard.MarkProcessed(envelope);

        // One rotation moves it into the previous generation, where membership is still checked. This is the
        // "at least Window / 2" half of the promise.
        theTime = theTime.Add(6.Minutes());

        guard.TryBeginProcessing(envelopeFor(envelope.Id)).ShouldBeFalse();
    }

    [Fact]
    public void an_id_ages_out_after_both_generations_rotate()
    {
        var guard = guardFor(10.Minutes());
        var envelope = envelopeFor();

        guard.TryBeginProcessing(envelope).ShouldBeTrue();
        guard.MarkProcessed(envelope);

        theTime = theTime.Add(6.Minutes());
        guard.TryBeginProcessing(envelopeFor(Guid.NewGuid())).ShouldBeTrue(); // forces the first rotation

        theTime = theTime.Add(6.Minutes());

        // Second rotation drops the generation the id was in -- it is genuinely forgotten now, which is what
        // keeps this from being an unbounded set.
        guard.TryBeginProcessing(envelopeFor(envelope.Id)).ShouldBeTrue();
        guard.TrackedCount.ShouldBe(0);
    }

    [Fact]
    public void memory_stays_bounded_under_a_sustained_flood_of_unique_ids()
    {
        // Deliberately a tiny ceiling and a window long enough that time-based rotation never fires: the only
        // thing keeping this bounded is the size trigger.
        var guard = guardFor(1.Hours(), 100);

        for (var i = 0; i < 50_000; i++)
        {
            var envelope = envelopeFor();
            guard.TryBeginProcessing(envelope).ShouldBeTrue();
            guard.MarkProcessed(envelope);

            guard.TrackedCount.ShouldBeLessThanOrEqualTo(100);
        }

        guard.TrackedCount.ShouldBeLessThanOrEqualTo(100);
        guard.InFlightCount.ShouldBe(0);
    }

    [Fact]
    public void in_flight_ids_are_bounded_too_even_if_nothing_ever_completes()
    {
        // The receivers release everything they claim, but a guard that could grow without limit when they
        // did not would be a memory leak on exactly the flood workload this feature exists for.
        var guard = guardFor(1.Hours(), 100);

        for (var i = 0; i < 50_000; i++)
        {
            guard.TryBeginProcessing(envelopeFor()).ShouldBeTrue();
        }

        guard.InFlightCount.ShouldBeLessThanOrEqualTo(100);
    }

    [Fact]
    public void size_based_eviction_uses_the_configured_ceiling()
    {
        var guard = guardFor(1.Hours(), 1000);

        for (var i = 0; i < 10_000; i++)
        {
            var envelope = envelopeFor();
            guard.TryBeginProcessing(envelope);
            guard.MarkProcessed(envelope);
        }

        guard.TrackedCount.ShouldBeLessThanOrEqualTo(1000);

        // ...and it really is tracking, not just throwing everything away
        guard.TrackedCount.ShouldBeGreaterThan(100);
    }

    [Fact]
    public void honors_MessageIdentity_IdOnly_across_destinations()
    {
        var guard = guardFor(identity: MessageIdentity.IdOnly);
        var id = Guid.NewGuid();

        guard.TryBeginProcessing(envelopeFor(id, "stub://one")).ShouldBeTrue();
        guard.TryBeginProcessing(envelopeFor(id, "stub://two")).ShouldBeFalse();
    }

    [Fact]
    public void honors_MessageIdentity_IdAndDestination()
    {
        var guard = guardFor(identity: MessageIdentity.IdAndDestination);
        var id = Guid.NewGuid();

        // The Modular Monolith shape: the same message id received at two different listening endpoints of
        // one process is two messages, not a duplicate.
        guard.TryBeginProcessing(envelopeFor(id, "stub://one")).ShouldBeTrue();
        guard.TryBeginProcessing(envelopeFor(id, "stub://two")).ShouldBeTrue();

        guard.TryBeginProcessing(envelopeFor(id, "stub://one")).ShouldBeFalse();
    }

    [Fact]
    public async Task exactly_one_caller_wins_each_id_under_concurrency()
    {
        var guard = new GenerationalIdempotencyGuard(new InMemoryIdempotencySettings(), MessageIdentity.IdOnly);

        var ids = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToArray();
        var winners = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();

        // Every id is offered to the guard by 8 threads at once. Duplicate suppression is only worth
        // anything if exactly one of them is told to go ahead.
        await Parallel.ForEachAsync(Enumerable.Range(0, 8), async (_, _) =>
        {
            await Task.Yield();

            foreach (var id in ids)
            {
                if (guard.TryBeginProcessing(envelopeFor(id)))
                {
                    winners.AddOrUpdate(id, 1, (_, count) => count + 1);
                }
            }
        });

        winners.Count.ShouldBe(ids.Length);
        winners.Values.ShouldAllBe(x => x == 1);
    }

    [Fact]
    public void the_window_has_to_be_positive()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new InMemoryIdempotencySettings { Window = TimeSpan.Zero });
    }

    [Fact]
    public void has_to_be_allowed_to_track_something()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new InMemoryIdempotencySettings { MaxTracked = 1 });
    }
}
