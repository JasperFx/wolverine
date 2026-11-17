using System.Net;
using Microsoft.Azure.Cosmos;
using Wolverine.ErrorHandling;

namespace Wolverine.CosmosDb;

/// <summary>
///     Recognizes Cosmos DB failures that say something about the moment rather than about the message: the
///     request was throttled or timed out, the service was unavailable, or a partition was moving. The same
///     message is expected to succeed later. Opt in with
///     <see cref="TransientCosmosDbFailureExtensions.OnTransientCosmosDbFailure" />.
/// </summary>
public static class TransientCosmosDbFailure
{
    /// <summary>
    ///     The 410 (Gone) sub status codes treated as transient: 0, 1000 (name cache is stale), 1002 (partition
    ///     key range gone), 1007 (completing split) and 1008 (completing partition migration).
    /// </summary>
    public static IReadOnlySet<int> RetryableGoneSubStatusCodes { get; } = new HashSet<int> { 0, 1000, 1002, 1007, 1008 };

    /// <summary>
    ///     True for a <see cref="CosmosException" /> with status 408 (request timeout), 429 (too many requests),
    ///     449 (retry with), 503 (service unavailable), 410 (gone) with one of the
    ///     <see cref="RetryableGoneSubStatusCodes" />, or 404 with sub status 1002 (read session not available),
    ///     unwrapped through <see cref="Exception.InnerException" />. Conflicts (409), failed preconditions
    ///     (412), bad requests and other permanent failures do not match.
    /// </summary>
    /// <remarks>
    ///     The Cosmos DB client already retries throttled requests itself and honours their
    ///     <see cref="CosmosException.RetryAfter" /> before it gives up and throws, so a 429 that reaches
    ///     Wolverine has used those retries up. The schedule you give this rule is what happens next.
    /// </remarks>
    public static bool Is(Exception? exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (e is CosmosException cosmos && isTransient(cosmos)) return true;
        }

        return false;
    }

    private static bool isTransient(CosmosException cosmos) => (int)cosmos.StatusCode switch
    {
        (int)HttpStatusCode.RequestTimeout => true,
        (int)HttpStatusCode.TooManyRequests => true,
        449 => true,
        (int)HttpStatusCode.ServiceUnavailable => true,
        (int)HttpStatusCode.Gone => RetryableGoneSubStatusCodes.Contains(cosmos.SubStatusCode),
        (int)HttpStatusCode.NotFound => cosmos.SubStatusCode == 1002,
        _ => false
    };
}

public static class TransientCosmosDbFailureExtensions
{
    /// <summary>
    ///     Match transient Cosmos DB failures (see <see cref="TransientCosmosDbFailure.Is" />) and choose what to
    ///     do with them, like any other <c>OnException()</c> rule. Nothing is retried unless you add an action.
    ///     Rules are first match wins, so declare this before a broader rule that would also match, such as one
    ///     for <see cref="CosmosException" />.
    /// </summary>
    /// <example>
    ///     <code>
    ///     opts.Policies.OnTransientCosmosDbFailure()
    ///         .ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds());
    ///     </code>
    /// </example>
    public static PolicyExpression OnTransientCosmosDbFailure(this IWithFailurePolicies policies)
    {
        return policies.OnException(TransientCosmosDbFailure.Is, "Transient Cosmos DB failure");
    }
}
