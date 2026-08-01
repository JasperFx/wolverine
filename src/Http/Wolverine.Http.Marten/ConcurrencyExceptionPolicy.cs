using JasperFx;
using JasperFx.CodeGeneration;
using Marten.Exceptions;
using Microsoft.AspNetCore.Http;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Middleware;

namespace Wolverine.Http.Marten;

/// <summary>
///     Companion policy to <see cref="MartenConcurrencyExceptionHandler" /> that registers the
///     ProblemDetails conflict response in the OpenAPI metadata of the HTTP chains where a Marten
///     concurrency failure is actually reachable. This policy does not touch the generated code --
///     the runtime mapping is done entirely by the exception handler middleware
/// </summary>
public class ConcurrencyExceptionPolicy : IChainPolicy
{
    private static readonly string[] _bodylessHttpMethods = ["GET", "HEAD"];

    private readonly int _statusCode;

    public ConcurrencyExceptionPolicy(int statusCode = 409)
    {
        _statusCode = statusCode;
    }

    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.OfType<HttpChain>().Where(shouldApply))
        {
            chain.Metadata.ProducesProblem(_statusCode);
        }
    }

    private static bool shouldApply(HttpChain chain)
    {
        var handlings = aggregateHandlingFor(chain);

        // Only the chains that could plausibly throw a Marten concurrency exception: the aggregate
        // handler workflow (FetchForWriting), and any route committing a Marten session through the
        // transactional middleware. Read-only GETs that merely commit an empty session (e.g.
        // [ReadAggregate] under AutoApplyTransactions) don't advertise an unreachable conflict
        // response to generated clients
        if (handlings.Count == 0)
        {
            if (!chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any())
            {
                return false;
            }

            if (chain.HttpMethods.All(x => _bodylessHttpMethods.Contains(x)))
            {
                return false;
            }
        }

        // When the endpoint's own OnException handlers already cover every exception type the
        // middleware would map, the exception never escapes the chain and the conflict response
        // is unreachable, so don't advertise it
        var reachable = new List<Type> { typeof(ConcurrencyException) };
        if (handlings.Any(x => x.LoadStyle == ConcurrencyStyle.Exclusive))
        {
            reachable.Add(typeof(StreamLockedException));
        }

        var caught = chain.Middleware.OfType<TryCatchFinallyFrame>()
            .SelectMany(x => x.CatchBlocks)
            .Select(x => x.ExceptionType)
            .ToArray();

        return !reachable.All(mapped => caught.Any(c => c.IsAssignableFrom(mapped)));
    }

    // AggregateHandling.Store keeps a single instance until a second stream on the same chain
    // promotes the tag to a list, and AggregateHandling.TryLoad only reads the single shape,
    // so read both shapes here to see exclusive loading on multi-stream chains too
    private static IReadOnlyList<AggregateHandling> aggregateHandlingFor(HttpChain chain)
    {
        if (chain.Tags.TryGetValue(nameof(AggregateHandling), out var raw))
        {
            if (raw is AggregateHandling single)
            {
                return [single];
            }

            if (raw is List<AggregateHandling> list)
            {
                return list;
            }
        }

        return [];
    }
}
