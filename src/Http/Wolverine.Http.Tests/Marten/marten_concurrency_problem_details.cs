using System.Text.Json;
using Alba;
using JasperFx;
using Marten;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-3764. A [WriteAggregate] style endpoint that loses an optimistic concurrency race used to
/// surface EventStreamUnexpectedMaxEventIdException as an unhandled 500 -- data integrity held,
/// but the HTTP contract leaked the internal failure.
///
/// <para>The fix is the OnException middleware convention rather than anything in the codegen
/// pipeline, so these tests exercise the documented recipe end to end. The second one matters
/// most: before Marten 10, StreamLockedException was a MartenException, NOT a ConcurrencyException,
/// so a recipe that caught only ConcurrencyException silently missed the FetchForExclusiveWriting
/// path. Marten 10 throws the shared JasperFx.Events.StreamLockedException, which derives from
/// ConcurrencyException, and the explicit lock-timeout handler stays beside the shared one.</para>
/// </summary>
public class marten_concurrency_problem_details(AppFixture fixture) : IntegrationContext(fixture)
{
    private async Task<Guid> createSeatHoldAsync()
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Url("/seatholds/create");
            x.StatusCodeShouldBeOk();
        });

        return JsonSerializer.Deserialize<Guid>(await result.ReadAsTextAsync());
    }

    [Fact]
    public async Task optimistic_concurrency_failure_becomes_a_409_problem_details()
    {
        var id = await createSeatHoldAsync();

        var result = await Host.Scenario(x =>
        {
            x.Post.Url($"/seatholds/{id}/optimistic-conflict");
            x.StatusCodeShouldBe(409);
            x.ContentTypeShouldBe("application/problem+json");
        });

        // EventStreamUnexpectedMaxEventIdException names the stream it could not append to
        (await result.ReadAsTextAsync()).ShouldContain("Unexpected starting version number");
    }

    [Fact]
    public async Task stream_locked_failure_becomes_a_409_problem_details()
    {
        var id = await createSeatHoldAsync();

        var store = Host.Services.GetRequiredService<IDocumentStore>();

        // Hold the exclusive lock on a separate connection for the duration of the request, so the
        // endpoint's own FetchForExclusiveWriting genuinely loses the race rather than being told
        // to throw. Marten uses a non-blocking try-lock here, so this fails fast
        await using var holder = store.LightweightSession();
        await holder.Events.FetchForExclusiveWriting<SeatHold>(id, TestContext.Current.CancellationToken);

        var result = await Host.Scenario(x =>
        {
            x.Post.Url($"/seatholds/{id}/exclusive");
            x.StatusCodeShouldBe(409);
            x.ContentTypeShouldBe("application/problem+json");
        });

        // Assert on the text StreamLockedException actually produces, so this cannot quietly start
        // passing on some other exception that also maps to 409
        (await result.ReadAsTextAsync()).ShouldContain("may be locked for updates");
    }

    [Fact]
    public async Task store_agnostic_stream_locked_failure_becomes_a_409_problem_details()
    {
        // GH-5142. JasperFx.Events.StreamLockedException is what Polecat's and Fisher's lock failures derive
        // from, and Marten 10's does too. Before Marten 10 the core catch was the only one that could turn this into a 409
        var id = Guid.NewGuid();

        var result = await Host.Scenario(x =>
        {
            x.Post.Url($"/seatholds/{id}/store-agnostic-lock");
            x.StatusCodeShouldBe(409);
            x.ContentTypeShouldBe("application/problem+json");
        });

        (await result.ReadAsTextAsync()).ShouldContain(id.ToString());
    }

    [Fact]
    public async Task marten_adds_its_own_lock_handler_only_while_its_type_is_not_the_jasperfx_one()
    {
        // GH-5142. The core catch for JasperFx.Events.StreamLockedException is always there. The Marten-specific
        // catch is added by MapMartenConcurrencyFailuresToConflict() only while Marten's StreamLockedException
        // does not derive from the JasperFx one, so this holds on both sides of Marten rebasing the type

        // The chain's code is generated on its first request
        await Host.Scenario(x =>
        {
            x.Post.Url($"/seatholds/{Guid.NewGuid()}/store-agnostic-lock");
            x.StatusCodeShouldBe(409);
        });

        var chain = Host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!
            .ChainFor("POST", "/seatholds/{id}/store-agnostic-lock");
        chain.ShouldNotBeNull();
        var code = chain.SourceCode.ShouldNotBeNull();

        code.ShouldContain("JasperFx.Events.StreamLockedException");

        var martenNeedsItsOwnCatch = !typeof(StreamLockedException)
            .IsAssignableTo(typeof(global::JasperFx.Events.StreamLockedException));
        code.Contains("Marten.Exceptions.StreamLockedException").ShouldBe(martenNeedsItsOwnCatch);
    }

    [Fact]
    public void stream_locked_now_derives_from_concurrency_exception_and_both_handlers_still_coexist()
    {
        // Marten 10 did what the earlier version of this test said should fail loudly: it moved
        // StreamLockedException under ConcurrencyException (the shared JasperFx.Events type, jasperfx#1169).
        // The explicit StreamLockedException handler is kept so the public recipe keeps its shape;
        // generated catch blocks are ordered most-specific first, so the two never collide (CS0160).
        typeof(StreamLockedException).IsAssignableTo(typeof(ConcurrencyException)).ShouldBeTrue();
        typeof(global::JasperFx.Events.EventStreamUnexpectedMaxEventIdException)
            .IsAssignableTo(typeof(ConcurrencyException)).ShouldBeTrue();
    }

    [Fact]
    public async Task several_failed_version_checks_in_one_commit_become_a_409()
    {
        var created = await Host.Scenario(x =>
        {
            x.Post.Url("/seatnotes/create");
            x.StatusCodeShouldBeOk();
        });

        var ids = JsonSerializer.Deserialize<Guid[]>(await created.ReadAsTextAsync())!;

        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new SeatNoteIds(ids)).ToUrl("/seatnotes/aggregate-conflict");
            x.StatusCodeShouldBe(409);
            x.ContentTypeShouldBe("application/problem+json");
        });

        (await result.ReadAsTextAsync()).ShouldContain("Optimistic concurrency check failed");
    }

    [Fact]
    public async Task an_aggregate_with_a_non_concurrency_failure_is_not_mapped_to_409()
    {
        await Host.Scenario(x =>
        {
            x.Post.Url("/seatnotes/mixed-aggregate");
            x.StatusCodeShouldBe(500);
        });
    }
}
