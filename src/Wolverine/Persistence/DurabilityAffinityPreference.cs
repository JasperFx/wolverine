using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence;

/// <summary>
///     GH-3785: the preference function handed to <see cref="AssignmentGrid.DistributeEvenlyWithAffinity" />,
///     plus a count of how much of it actually engaged.
///
///     <para>The affinity join is deliberately fail-silent — <see cref="DurabilityProjectionAffinity" /> falls
///     back to the even spread whenever it cannot match a durability agent to a projection owner, because a
///     miss is never <i>wrong</i>, only not-better. That is the right runtime behavior and the wrong
///     diagnostic story: a join that never fires because the two families spell the same database differently
///     looks exactly like the feature working, minus the benefit. Short of joining <c>pg_stat_activity</c>
///     against the assignment table on a production cluster, there was no way to tell the two apart.</para>
///
///     <para>So count the two outcomes and let the caller report them. <see cref="Considered" /> counts only
///     durability agents whose URI carries a (server, database) — the null store and the multi-tenanted
///     composite marker are not databases and would otherwise show up as permanent misses.</para>
/// </summary>
internal sealed class DurabilityAffinityPreference
{
    private readonly Func<Uri, AssignmentGrid.Node?> _resolve;

    internal DurabilityAffinityPreference(Func<Uri, AssignmentGrid.Node?> resolve, int knownDatabases)
    {
        _resolve = resolve;
        KnownDatabases = knownDatabases;
    }

    /// <summary>
    ///     How many distinct databases had event-subscription agents in the grid to follow. Zero is the
    ///     ordinary case for an application with no Marten projections — nothing to co-locate with, and
    ///     nothing worth reporting.
    /// </summary>
    public int KnownDatabases { get; }

    /// <summary>
    ///     Durability agents whose URI named a database, and so were eligible to follow a projection owner.
    /// </summary>
    public int Considered { get; private set; }

    /// <summary>
    ///     Of those, how many resolved to the node already owning that database's projections.
    /// </summary>
    public int Matched { get; private set; }

    public AssignmentGrid.Node? NodeFor(Uri uri)
    {
        if (DurabilityProjectionAffinity.DatabaseOf(uri) == null)
        {
            return null;
        }

        Considered++;

        var node = _resolve(uri);
        if (node != null)
        {
            Matched++;
        }

        return node;
    }

    /// <summary>
    ///     Write the outcome to the log, but only when it has changed since the last evaluation. Assignment
    ///     is re-evaluated on every health check, and a settled cluster reports the same numbers forever — an
    ///     unconditional log line would be pure noise at exactly the cadence that makes it unreadable. A
    ///     change means a deploy, a rebalance, or a database coming and going, which is when an operator
    ///     wants to see it.
    /// </summary>
    public void ReportTo(ILogger logger, ref (int Known, int Considered, int Matched) last)
    {
        var current = (KnownDatabases, Considered, Matched);
        if (current == last)
        {
            return;
        }

        last = current;

        if (KnownDatabases == 0 || Considered == 0)
        {
            // Nothing to co-locate with. Not a failure, and not worth an operator's attention.
            return;
        }

        if (Matched == 0)
        {
            // The fail-silent case, and the only one that needs a warning: there ARE projection agents and
            // there ARE database-bearing durability agents, and not one of them joined. On a multi-database
            // store that is a spelling divergence between the two descriptor pipelines, not a coincidence.
            logger.LogWarning(
                "Durability/projection database affinity (GH-3785) matched none of {Considered} durability agents against {KnownDatabases} databases holding event subscription agents. Durability agents fall back to an even spread, so each of those databases will attract two nodes' connection pools instead of one. This usually means the durability and event subscription URIs spell the same database differently.",
                Considered, KnownDatabases);

            return;
        }

        logger.LogInformation(
            "Durability/projection database affinity (GH-3785) co-located {Matched} of {Considered} durability agents with their database's event subscription agents across {KnownDatabases} databases",
            Matched, Considered, KnownDatabases);
    }
}
