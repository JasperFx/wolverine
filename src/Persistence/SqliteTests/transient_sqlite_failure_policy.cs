using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Sqlite;
using Xunit;

namespace SqliteTests;

public class transient_sqlite_failure_policy
{
    private static SqliteException Transient() => new("database is locked", 5);
    private static SqliteException Permanent() => new("UNIQUE constraint failed", 19, 2067);

    [Theory]
    [InlineData(5, 5)] // SQLITE_BUSY
    [InlineData(5, 261)] // SQLITE_BUSY_RECOVERY
    [InlineData(5, 517)] // SQLITE_BUSY_SNAPSHOT
    [InlineData(5, 773)] // SQLITE_BUSY_TIMEOUT
    [InlineData(6, 6)] // SQLITE_LOCKED
    [InlineData(6, 262)] // SQLITE_LOCKED_SHAREDCACHE
    [InlineData(0, 517)] // only the extended code says busy
    public void busy_and_locked_match(int errorCode, int extendedErrorCode)
    {
        TransientSqliteFailure.Is(new SqliteException("busy", errorCode, extendedErrorCode)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(19, 2067)] // SQLITE_CONSTRAINT_UNIQUE
    [InlineData(19, 787)] // SQLITE_CONSTRAINT_FOREIGNKEY
    [InlineData(1, 1)] // SQLITE_ERROR, e.g. a syntax error
    [InlineData(8, 8)] // SQLITE_READONLY
    [InlineData(11, 11)] // SQLITE_CORRUPT
    public void permanent_errors_do_not_match(int errorCode, int extendedErrorCode)
    {
        TransientSqliteFailure.Is(new SqliteException("boom", errorCode, extendedErrorCode)).ShouldBeFalse();
    }

    [Fact]
    public void wrapped_exceptions_are_unwrapped()
    {
        // Like EF Core's DbUpdateException or a Fisher exception wrapping the SqliteException, at any depth
        TransientSqliteFailure.Is(new InvalidOperationException("outer", new Exception("middle", Transient())))
            .ShouldBeTrue();
        TransientSqliteFailure.Is(new InvalidOperationException("outer", new Exception("middle", Permanent())))
            .ShouldBeFalse();
    }

    [Fact]
    public void unrelated_exceptions_do_not_match()
    {
        TransientSqliteFailure.Is(null).ShouldBeFalse();
        TransientSqliteFailure.Is(new InvalidOperationException()).ShouldBeFalse();
        TransientSqliteFailure.Is(new TimeoutException()).ShouldBeFalse();
    }

    [Fact]
    public void it_registers_one_rule_with_the_chosen_action()
    {
        var policies = new FailurePoliciesHolder(new FailureRuleCollection());

        policies.OnTransientSqliteFailure().ScheduleRetry(1.Seconds(), 5.Seconds());

        var rule = policies.Failures.ShouldHaveSingleItem();
        rule.Match.Matches(Transient()).ShouldBeTrue();
        rule.Match.Matches(Permanent()).ShouldBeFalse();
        rule.Count().ShouldBe(2);
    }

    private class FailurePoliciesHolder(FailureRuleCollection failures) : IWithFailurePolicies
    {
        public FailureRuleCollection Failures { get; } = failures;
    }
}
