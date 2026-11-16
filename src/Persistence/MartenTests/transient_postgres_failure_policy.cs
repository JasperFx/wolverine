using System.Net.Sockets;
using JasperFx.Core;
using Marten.Exceptions;
using Npgsql;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Xunit;

namespace MartenTests;

public class transient_postgres_failure_policy
{
    private static NpgsqlException Transient() => new("connection lost", new IOException("reset", new SocketException()));
    private static NpgsqlException Permanent() => PostgresException("23505");

    private static NpgsqlException PostgresException(string sqlState) =>
        new PostgresException("boom", "ERROR", "ERROR", sqlState);

    [Fact]
    public void a_transient_npgsql_exception_matches()
    {
        var ex = Transient();
        ex.IsTransient.ShouldBeTrue();
        TransientPostgresFailure.Is(ex).ShouldBeTrue();
    }

    [Fact]
    public void a_permanent_npgsql_exception_does_not_match()
    {
        var ex = Permanent();
        ex.IsTransient.ShouldBeFalse();
        TransientPostgresFailure.Is(ex).ShouldBeFalse();
    }

    [Fact]
    public void a_marten_command_exception_is_unwrapped()
    {
        TransientPostgresFailure.Is(new MartenCommandException(new NpgsqlCommand("select 1"), Transient())).ShouldBeTrue();
        TransientPostgresFailure.Is(new MartenCommandException(new NpgsqlCommand("select 1"), Permanent())).ShouldBeFalse();
    }

    [Fact]
    public void a_disposed_npgsql_transaction_matches()
    {
        TransientPostgresFailure.Is(new ObjectDisposedException(nameof(NpgsqlTransaction))).ShouldBeTrue();
        TransientPostgresFailure.Is(new ObjectDisposedException("Something else")).ShouldBeFalse();
    }

    [Fact]
    public void unrelated_exceptions_do_not_match()
    {
        TransientPostgresFailure.Is(new InvalidOperationException()).ShouldBeFalse();
        TransientPostgresFailure.Is(new TimeoutException()).ShouldBeFalse();
    }

    [Fact]
    public void it_registers_one_rule_with_the_chosen_action()
    {
        var policies = new FailurePoliciesHolder(new FailureRuleCollection());

        policies.OnTransientPostgresFailure().ScheduleRetry(1.Seconds(), 5.Seconds());

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
