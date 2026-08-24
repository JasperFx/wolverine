using System.Text.Json;
using Alba;
using JasperFx;
using Marten;
using Marten.Exceptions;
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
/// most: StreamLockedException is a MartenException, NOT a ConcurrencyException, so a recipe that
/// catches only ConcurrencyException silently misses the FetchForExclusiveWriting path.</para>
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
    public void the_two_exception_types_are_unrelated_which_is_why_both_handlers_exist()
    {
        // Pins the reason the middleware needs two OnException methods. If Marten ever moves
        // StreamLockedException under ConcurrencyException this test should fail loudly rather
        // than leaving a redundant handler in the docs
        typeof(StreamLockedException).IsAssignableTo(typeof(ConcurrencyException)).ShouldBeFalse();
        typeof(global::JasperFx.Events.EventStreamUnexpectedMaxEventIdException)
            .IsAssignableTo(typeof(ConcurrencyException)).ShouldBeTrue();
    }
}
