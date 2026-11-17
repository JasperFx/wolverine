using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Cluster;
using Raven.Client.Exceptions.Database;
using Wolverine.ErrorHandling;

namespace Wolverine.RavenDb;

/// <summary>
///     Recognizes RavenDB failures that say something about the moment rather than about the message: no node
///     of the cluster could be reached, a request timed out, the cluster had no leader, or the database was
///     still loading. The same message is expected to succeed later. Opt in with
///     <see cref="TransientRavenDbFailureExtensions.OnTransientRavenDbFailure" />.
/// </summary>
public static class TransientRavenDbFailure
{
    /// <summary>
    ///     True for <see cref="AllTopologyNodesDownException" /> (which is also what the client throws after
    ///     HTTP 408, 502, 503 and 504 responses from every node), <see cref="RequestedNodeUnavailableException" />,
    ///     <see cref="UnsuccessfulRequestException" />, <see cref="RavenTimeoutException" />,
    ///     <see cref="NodeIsPassiveException" />, <see cref="NoLeaderException" />,
    ///     <see cref="DatabaseLoadTimeoutException" />, <see cref="DatabaseConcurrentLoadTimeoutException" /> and
    ///     <see cref="DatabaseIdleException" />, unwrapped through <see cref="Exception.InnerException" />.
    ///     A <see cref="ConcurrencyException" /> does not match: another writer got there first, and retrying
    ///     the same change will not change that. Handle it with its own rule if your handler reloads its state.
    /// </summary>
    public static bool Is(Exception? exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (isTransient(e)) return true;
        }

        return false;
    }

    private static bool isTransient(Exception exception) => exception is AllTopologyNodesDownException
        or RequestedNodeUnavailableException
        or UnsuccessfulRequestException
        or RavenTimeoutException
        or NodeIsPassiveException
        or NoLeaderException
        or DatabaseLoadTimeoutException
        or DatabaseConcurrentLoadTimeoutException
        or DatabaseIdleException;
}

public static class TransientRavenDbFailureExtensions
{
    /// <summary>
    ///     Match transient RavenDB failures (see <see cref="TransientRavenDbFailure.Is" />) and choose what to
    ///     do with them, like any other <c>OnException()</c> rule. Nothing is retried unless you add an action.
    ///     Rules are first match wins, so declare this before a broader rule that would also match, such as one
    ///     for <see cref="RavenException" />.
    /// </summary>
    /// <example>
    ///     <code>
    ///     opts.Policies.OnTransientRavenDbFailure()
    ///         .ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds());
    ///     </code>
    /// </example>
    public static PolicyExpression OnTransientRavenDbFailure(this IWithFailurePolicies policies)
    {
        return policies.OnException(TransientRavenDbFailure.Is, "Transient RavenDB failure");
    }
}
