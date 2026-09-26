using Microsoft.AspNetCore.Http.Metadata;
using Shouldly;
using WolverineWebApi.Marten;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-4512. The documented recipe was four pieces a user had to know about and get right: a middleware class
/// with two OnException overloads, an AddMiddleware registration, and a separate IHttpPolicy purely to stamp
/// the OpenAPI metadata. MapMartenConcurrencyFailuresToConflict() is all four.
///
/// <para>
/// The 409 responses themselves are covered end to end by marten_concurrency_problem_details, which now runs
/// against this opt in rather than the hand written recipe. This class covers the piece that used to be the
/// easiest to forget.
/// </para>
/// </summary>
public class conflict_mapping_openapi_4512(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public void the_conflict_is_advertised_in_the_openapi_document()
    {
        var chains = HttpChains.Chains
            .Where(x => x.Method.HandlerType == typeof(ConcurrencyEndpoints))
            .ToArray();

        chains.ShouldNotBeEmpty();

        foreach (var chain in chains)
        {
            var produces = chain.BuildEndpoint(RouteWarmup.Lazy)
                .Metadata
                .OfType<IProducesResponseTypeMetadata>()
                .Select(x => x.StatusCode)
                .ToArray();

            produces.ShouldContain(409,
                $"{chain.Method.HandlerType.Name}.{chain.Method.Method.Name} does not advertise a 409");
        }
    }

    [Fact]
    public void chains_outside_the_filter_are_left_alone()
    {
        // The opt in takes the same predicate AddMiddleware does, and must not reach past it -- stamping
        // every transactional chain in an application with a 409 it cannot actually produce would be its
        // own kind of wrong.
        var others = HttpChains.Chains
            .Where(x => x.Method.HandlerType != typeof(ConcurrencyEndpoints))
            .Take(25)
            .ToArray();

        others.ShouldNotBeEmpty();

        foreach (var chain in others)
        {
            chain.BuildEndpoint(RouteWarmup.Lazy)
                .Metadata
                .OfType<IProducesResponseTypeMetadata>()
                .Select(x => x.StatusCode)
                .ShouldNotContain(409,
                    $"{chain.Method.HandlerType.Name}.{chain.Method.Method.Name} advertises a 409 it cannot produce");
        }
    }
}
