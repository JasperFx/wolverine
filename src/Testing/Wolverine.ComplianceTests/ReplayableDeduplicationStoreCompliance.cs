using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>GH-4742. The <see cref="IReplayableDeduplicationStore" /> contract, run against every RDBMS store.</summary>
public abstract class ReplayableDeduplicationStoreCompliance : IAsyncLifetime
{
    private IHost _host = null!;
    private IReplayableDeduplicationStore theStore = null!;

    protected abstract void configurePersistence(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.EnableDeduplicatedResponses = true;
                opts.Durability.DeduplicationWindow = 1.Hours();

                // Small, so reaping takes several batches.
                opts.Durability.DeduplicationCleanupBatchSize = 2;
                opts.Discovery.DisableConventionalDiscovery();

                configurePersistence(opts);
            }).StartAsync();

        await _host.ResetResourceState();

        theStore = _host.GetRuntime().Storage.ShouldBeAssignableTo<IReplayableDeduplicationStore>()!;
    }

    public virtual async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    // One attempt's claim token, unless a test needs another.
    private const string Token = "attempt-1";

    private static string newId() => Guid.NewGuid().ToString();

    private static DateTimeOffset expires() => DateTimeOffset.UtcNow.AddHours(1);

    [Fact]
    public void the_store_is_enabled()
    {
        theStore.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task a_claim_records_its_fingerprint_and_has_no_response_yet()
    {
        var id = newId();

        (await theStore.TryClaimAsync(id, "fingerprint-1", Token, expires())).ShouldBeTrue();

        var stored = await theStore.FindAsync(id);
        stored.ShouldNotBeNull();
        stored.Fingerprint.ShouldBe("fingerprint-1");
        stored.Response.ShouldBeNull();
    }

    [Fact]
    public async Task a_second_claim_of_the_same_id_loses()
    {
        var id = newId();

        (await theStore.TryClaimAsync(id, "first", Token, expires())).ShouldBeTrue();
        (await theStore.TryClaimAsync(id, "second", Token, expires())).ShouldBeFalse();

        // The loser does not overwrite the winner's fingerprint.
        (await theStore.FindAsync(id))!.Fingerprint.ShouldBe("first");
    }

    [Fact]
    public async Task an_unknown_id_has_no_claim()
    {
        (await theStore.FindAsync(newId())).ShouldBeNull();
    }

    [Fact]
    public async Task recording_a_response_round_trips_it()
    {
        var id = newId();
        await theStore.TryClaimAsync(id, "fingerprint", Token, expires());

        // Large and non-ASCII.
        var body = "{\"name\":\"" + new string('é', 6000) + "\"}";
        (await theStore.RecordResponseAsync(id, Token, new DeduplicatedResponse(201, body, "/orders/42"))).ShouldBeTrue();

        var stored = await theStore.FindAsync(id);
        stored!.Response.ShouldNotBeNull();
        stored.Response.StatusCode.ShouldBe(201);
        stored.Response.Body.ShouldBe(body);
        stored.Response.Location.ShouldBe("/orders/42");
        stored.Fingerprint.ShouldBe("fingerprint");
    }

    [Fact]
    public async Task a_response_without_a_body_or_location_round_trips()
    {
        var id = newId();
        await theStore.TryClaimAsync(id, "fingerprint", Token, expires());
        await theStore.RecordResponseAsync(id, Token, new DeduplicatedResponse(200, null, null));

        var response = (await theStore.FindAsync(id))!.Response!;
        response.StatusCode.ShouldBe(200);
        response.Body.ShouldBeNull();
        response.Location.ShouldBeNull();
    }

    [Fact]
    public async Task an_answered_claim_is_never_answered_again()
    {
        // A request outliving a reaped claim must not overwrite the answer of the one that took it next.
        var id = newId();
        await theStore.TryClaimAsync(id, "fingerprint", Token, expires());
        await theStore.RecordResponseAsync(id, Token, new DeduplicatedResponse(201, "first", null));

        (await theStore.RecordResponseAsync(id, Token, new DeduplicatedResponse(200, "second", null))).ShouldBeFalse();

        (await theStore.FindAsync(id))!.Response!.Body.ShouldBe("first");
    }

    [Fact]
    public async Task a_stale_attempt_cannot_record_on_or_release_its_successors_claim()
    {
        // Attempt 1's claim expired mid-request and was reaped; attempt 2 took the key.
        var id = newId();
        await theStore.TryClaimAsync(id, "fingerprint", Token, DateTimeOffset.UtcNow.AddSeconds(-5));
        await theStore.DeleteExpiredAsync(DateTimeOffset.UtcNow);
        (await theStore.TryClaimAsync(id, "fingerprint", "attempt-2", expires())).ShouldBeTrue();

        (await theStore.RecordResponseAsync(id, Token, new DeduplicatedResponse(201, "stale", null))).ShouldBeFalse();
        await theStore.ReleaseUnansweredAsync(id, Token);

        var stored = await theStore.FindAsync(id);
        stored.ShouldNotBeNull();
        stored.Response.ShouldBeNull();

        (await theStore.RecordResponseAsync(id, "attempt-2", new DeduplicatedResponse(201, "fresh", null))).ShouldBeTrue();
    }

    [Fact]
    public async Task recording_on_an_unknown_id_records_nothing()
    {
        (await theStore.RecordResponseAsync(newId(), Token, new DeduplicatedResponse(201, "{}", null))).ShouldBeFalse();
    }

    [Fact]
    public async Task releasing_an_unanswered_claim_frees_the_id()
    {
        var id = newId();
        await theStore.TryClaimAsync(id, "fingerprint", Token, expires());

        await theStore.ReleaseUnansweredAsync(id, Token);

        (await theStore.FindAsync(id)).ShouldBeNull();
        (await theStore.TryClaimAsync(id, "fingerprint", Token, expires())).ShouldBeTrue();
    }

    [Fact]
    public async Task releasing_an_answered_claim_keeps_it()
    {
        // A failure after the response is recorded must not free the id.
        var id = newId();
        await theStore.TryClaimAsync(id, "fingerprint", Token, expires());
        await theStore.RecordResponseAsync(id, Token, new DeduplicatedResponse(201, "{}", null));

        await theStore.ReleaseUnansweredAsync(id, Token);

        (await theStore.FindAsync(id))!.Response.ShouldNotBeNull();
    }

    [Fact]
    public async Task releasing_an_unclaimed_id_is_a_no_op()
    {
        await theStore.ReleaseUnansweredAsync(newId(), Token);
    }

    [Fact]
    public async Task concurrent_claims_produce_exactly_one_winner()
    {
        var id = newId();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => theStore.TryClaimAsync(id, $"fingerprint-{i}", Token, expires())));

        results.Count(x => x).ShouldBe(1);
    }

    [Fact]
    public async Task the_reaper_removes_expired_claims_answered_or_not()
    {
        var answered = newId();
        var unanswered = newId();
        var live = newId();
        var moreExpired = Enumerable.Range(0, 4).Select(_ => newId()).ToArray();

        await theStore.TryClaimAsync(answered, "fingerprint", Token, DateTimeOffset.UtcNow.AddSeconds(-5));
        await theStore.RecordResponseAsync(answered, Token, new DeduplicatedResponse(201, "{}", null));
        await theStore.TryClaimAsync(unanswered, "fingerprint", Token, DateTimeOffset.UtcNow.AddSeconds(-5));
        await theStore.TryClaimAsync(live, "fingerprint", Token, expires());
        foreach (var expired in moreExpired)
        {
            await theStore.TryClaimAsync(expired, "fingerprint", Token, DateTimeOffset.UtcNow.AddSeconds(-5));
        }

        (await theStore.DeleteExpiredAsync(DateTimeOffset.UtcNow)).ShouldBeGreaterThanOrEqualTo(6);

        (await theStore.FindAsync(answered)).ShouldBeNull();
        (await theStore.FindAsync(unanswered)).ShouldBeNull();
        (await theStore.FindAsync(live)).ShouldNotBeNull();
    }
}
