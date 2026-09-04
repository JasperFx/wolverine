using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace OracleTests.Agents;

/// <summary>
/// GH-4275. The Oracle counterpart of <c>Bug_advisory_lock_stacking_blocks_failover</c>: since the
/// heartbeat-renewal change in <c>a84d6a262</c>, <c>NodeAgentController.DoHealthChecksInternalAsync</c>
/// calls <c>TryAttainLeadershipLockAsync</c> on EVERY tick, including ticks where this node is already the
/// leader.
///
/// <para>Oracle holds its row lock in an uncommitted transaction on a dedicated connection — that is how the
/// lock is held — so the renewal used to open a SECOND connection whose <c>SELECT ... FOR UPDATE NOWAIT</c>
/// was blocked by this node's own first transaction and raised ORA-00054. The renewal answered
/// <c>false</c> for a lock the node holds, and the controller reads a false renewal as lost leadership:</para>
///
/// <code>
/// if (IsLeader)
/// {
///     await stepDownAsync("the leadership advisory lock could not be renewed");
/// }
/// </code>
///
/// <para>So a sitting Oracle leader stepped down on the very next tick after being elected, every tick — and
/// never reached <c>EvaluateAssignmentsAsync</c>, which is only on the <c>true</c> branch, so the leader's
/// actual work of evaluating agent assignments did not run at all.</para>
///
/// <para>The existing leadership compliance suite could not catch this: every one of its tests is about a
/// TRANSITION — becoming leader, switchover, ejecting a stale node — and none assert that a leader stays
/// leader across repeated health-check ticks. A node that steps down also usually re-attains immediately,
/// being the only candidate, so the churn leaves the end state those tests assert on untouched.</para>
/// </summary>
public class Bug_4275_leadership_renewal_is_reentrant : OracleContext
{
    // A lock id PER TEST. Oracle holds the row lock in an uncommitted transaction, so it is only freed
    // once the holder's rollback and close have completed -- sharing one id across two tests in this
    // collection let the second test's first attain race the first test's teardown and hit ORA-00054.
    // Bobcat caught that as a flaky pass-on-retry rather than a failure.
    private const int RenewalLockId = unchecked((int)0xB0BCB0);
    private const int ReleaseLockId = unchecked((int)0xB0BCB1);
    private const string SchemaName = "WOLVERINE";

    [Fact]
    public async Task the_renewal_reports_the_lock_still_held()
    {
        await using var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        await migrateAsync(dataSource);

        var advisoryLock = new OracleAdvisoryLock(dataSource, NullLogger.Instance, SchemaName);

        try
        {
            (await advisoryLock.TryAttainLockAsync(RenewalLockId, CancellationToken.None))
                .ShouldBeTrue("the first attain elects this node");

            // Ten heartbeat ticks on the same leader. Every one of these used to answer false, and every
            // false is a stepDownAsync.
            for (var tick = 0; tick < 10; tick++)
            {
                (await advisoryLock.TryAttainLockAsync(RenewalLockId, CancellationToken.None))
                    .ShouldBeTrue($"the renewal on tick {tick} must report the lock still held");

                advisoryLock.HasLock(RenewalLockId).ShouldBeTrue($"and the lock is still held on tick {tick}");
            }
        }
        finally
        {
            await advisoryLock.DisposeAsync();
        }
    }

    [Fact]
    public async Task one_release_after_many_renewals_actually_releases()
    {
        // The other half, and the reason the short-circuit is the right shape rather than swallowing
        // ORA-00054: renewals must not accumulate anything a single release cannot undo. A contender has
        // to be able to take the lock after exactly one ReleaseLockAsync, which is what stepDownAsync and
        // DisableAgentsAsync issue.
        await using var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        await migrateAsync(dataSource);

        var holder = new OracleAdvisoryLock(dataSource, NullLogger.Instance, SchemaName);
        (await holder.TryAttainLockAsync(ReleaseLockId, CancellationToken.None)).ShouldBeTrue();

        for (var tick = 0; tick < 10; tick++)
        {
            await holder.TryAttainLockAsync(ReleaseLockId, CancellationToken.None);
        }

        await holder.ReleaseLockAsync(ReleaseLockId);

        var contender = new OracleAdvisoryLock(dataSource, NullLogger.Instance, SchemaName);
        try
        {
            (await contender.TryAttainLockAsync(ReleaseLockId, CancellationToken.None))
                .ShouldBeTrue("a single release after repeated renewals has to free the lock for a new leader");
        }
        finally
        {
            await contender.DisposeAsync();
            await holder.DisposeAsync();
        }
    }

    private static async Task migrateAsync(OracleDataSource dataSource)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.OracleConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        await using var store = new OracleMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<OracleMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await store.Admin.MigrateAsync();
    }
}
