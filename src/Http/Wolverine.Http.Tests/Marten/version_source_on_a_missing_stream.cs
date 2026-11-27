using System.Text.Json;
using Alba;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Persistence.EventSourcing;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// A [WriteAggregate] with an expected version used to pass the version to FetchForWriting(id, version), which throws
/// a ConcurrencyException for a stream that does not exist before the missing aggregate handling could run. RFC 9110
/// §13.2.1: a request that would fail without its precondition fails for that reason, so a missing stream is a 404 and
/// only an existing stream at another version is a concurrency failure. These endpoints map concurrency failures to 409.
/// </summary>
public class version_source_on_a_missing_stream(AppFixture fixture) : IntegrationContext(fixture)
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

    private async Task<StreamState?> streamStateAsync(Guid id)
    {
        await using var session = Store.LightweightSession();
        return await session.Events.FetchStreamStateAsync(id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task missing_stream_with_an_expected_version_is_a_404()
    {
        var id = Guid.NewGuid();

        await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{id}/confirm-at/3");
            x.StatusCodeShouldBe(404);
        });

        (await streamStateAsync(id)).ShouldBeNull();
    }

    [Fact]
    public async Task missing_stream_with_an_expected_version_of_zero_is_a_404()
    {
        var id = Guid.NewGuid();

        await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{id}/confirm-at/0");
            x.StatusCodeShouldBe(404);
        });

        (await streamStateAsync(id)).ShouldBeNull();
    }

    [Fact]
    public async Task missing_stream_with_an_expected_version_follows_on_missing()
    {
        var id = Guid.NewGuid();

        await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{id}/confirm-at/3/problem-details");
            x.StatusCodeShouldBe(404);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }

    [Fact]
    public async Task missing_stream_that_is_not_required_is_still_a_concurrency_failure()
    {
        // The endpoint would run and start the stream, so the version is the only precondition that fails
        var id = Guid.NewGuid();

        var result = await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{id}/confirm-at/3/optional");
            x.StatusCodeShouldBe(409);
        });

        (await result.ReadAsTextAsync()).ShouldContain("Expected the existing version to be 3, but was 0");
        (await streamStateAsync(id)).ShouldBeNull();
    }

    [Fact]
    public async Task existing_stream_at_another_version_is_a_concurrency_failure()
    {
        var id = await createSeatHoldAsync();

        var result = await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{id}/confirm-at/5");
            x.StatusCodeShouldBe(409);
            x.ContentTypeShouldBe("application/problem+json");
        });

        (await result.ReadAsTextAsync()).ShouldContain("Expected the existing version to be 5, but was 1");
        (await streamStateAsync(id))!.Version.ShouldBe(1);
    }

    [Fact]
    public async Task existing_stream_at_the_expected_version_is_written()
    {
        var id = await createSeatHoldAsync();

        await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{id}/confirm-at/1");
            x.StatusCodeShouldBe(204);
        });

        (await streamStateAsync(id))!.Version.ShouldBe(2);
    }

    [Fact]
    public async Task stream_that_moves_after_the_load_is_still_refused_at_commit()
    {
        var id = await createSeatHoldAsync();

        await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{id}/confirm-at/1/after-another-writer");
            x.StatusCodeShouldBe(409);
            x.ContentTypeShouldBe("application/problem+json");
        });

        // Only the other writer's event was appended
        (await streamStateAsync(id))!.Version.ShouldBe(2);
    }

    [Fact]
    public async Task the_stream_is_loaded_without_the_version_and_checked_after_the_missing_aggregate()
    {
        // The chain's code is generated on its first request
        await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{Guid.NewGuid()}/confirm-at/1");
            x.StatusCodeShouldBe(404);
        });

        var chain = Host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!
            .ChainFor("POST", "/seatholds/{id}/confirm-at/{expectedVersion}");
        var code = chain.ShouldNotBeNull().SourceCode.ShouldNotBeNull();

        var fetch = code.Split('\n').Single(x => x.Contains($"FetchForWriting<{typeof(SeatHold).FullNameInCode()}>"));
        fetch.ShouldNotContain("expectedVersion");

        var missing = code.IndexOf("404", StringComparison.Ordinal);
        var versionCheck = code.IndexOf(
            $"{typeof(ExpectedStreamVersion).FullNameInCode()}.{nameof(ExpectedStreamVersion.AssertStartingVersion)}(",
            StringComparison.Ordinal);

        missing.ShouldBeGreaterThan(0);
        versionCheck.ShouldBeGreaterThan(missing);
    }

    [Fact]
    public async Task a_missing_second_aggregate_is_a_404_even_when_the_first_is_stale()
    {
        // The first aggregate's version check used to run before the second aggregate's missing aggregate handling
        var from = await createSeatHoldAsync();
        var to = Guid.NewGuid();

        await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{from}/{to}/move-at/5/0");
            x.StatusCodeShouldBe(404);
        });

        (await streamStateAsync(to)).ShouldBeNull();
    }

    [Fact]
    public async Task a_stale_first_aggregate_is_a_concurrency_failure_when_both_exist()
    {
        var from = await createSeatHoldAsync();
        var to = await createSeatHoldAsync();

        var result = await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{from}/{to}/move-at/5/1");
            x.StatusCodeShouldBe(409);
        });

        (await result.ReadAsTextAsync()).ShouldContain("Expected the existing version to be 5, but was 1");
        (await streamStateAsync(to))!.Version.ShouldBe(1);
    }

    [Fact]
    public async Task two_aggregates_at_their_expected_versions_are_written()
    {
        var from = await createSeatHoldAsync();
        var to = await createSeatHoldAsync();

        await Scenario(x =>
        {
            x.Post.Url($"/seatholds/{from}/{to}/move-at/1/1");
            x.StatusCodeShouldBe(204);
        });

        (await streamStateAsync(to))!.Version.ShouldBe(2);
    }
}
