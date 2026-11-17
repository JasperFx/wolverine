using System.Net.Sockets;
using System.Reflection;
using JasperFx.Core;
using MySqlConnector;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.MySql;

namespace MySqlTests;

public class transient_mysql_failure_policy
{
    private static MySqlException Transient() => MySqlError(MySqlErrorCode.LockDeadlock);
    private static MySqlException Permanent() => MySqlError(MySqlErrorCode.DuplicateKeyEntry);

    [Theory]
    [InlineData(MySqlErrorCode.LockDeadlock)]
    [InlineData(MySqlErrorCode.LockWaitTimeout)]
    [InlineData(MySqlErrorCode.UnableToConnectToHost)]
    [InlineData(MySqlErrorCode.ConnectionCountError)]
    [InlineData(MySqlErrorCode.ServerShutdown)]
    [InlineData(MySqlErrorCode.AbortingConnection)]
    [InlineData(MySqlErrorCode.ClientInteractionTimeout)]
    [InlineData(MySqlErrorCode.CommandTimeoutExpired)]
    public void transient_errors_match(MySqlErrorCode code)
    {
        TransientMySqlFailure.Is(MySqlError(code)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(MySqlErrorCode.DuplicateKeyEntry)]
    [InlineData(MySqlErrorCode.NoReferencedRow2)] // foreign key violation
    [InlineData(MySqlErrorCode.ParseError)] // syntax error
    [InlineData(MySqlErrorCode.NoSuchTable)]
    public void permanent_errors_do_not_match(MySqlErrorCode code)
    {
        TransientMySqlFailure.Is(MySqlError(code)).ShouldBeFalse();
    }

    [Fact]
    public void a_lost_connection_matches()
    {
        TransientMySqlFailure.Is(MySqlError(MySqlErrorCode.None, new EndOfStreamException())).ShouldBeTrue();
        TransientMySqlFailure.Is(MySqlError(MySqlErrorCode.None, new IOException("reset", new SocketException())))
            .ShouldBeTrue();
        TransientMySqlFailure.Is(MySqlError(MySqlErrorCode.None, new InvalidCastException())).ShouldBeFalse();
    }

    [Fact]
    public void wrapped_exceptions_are_unwrapped()
    {
        TransientMySqlFailure.Is(new InvalidOperationException("outer", new Exception("middle", Transient())))
            .ShouldBeTrue();
        TransientMySqlFailure.Is(new InvalidOperationException("outer", new Exception("middle", Permanent())))
            .ShouldBeFalse();
    }

    [Fact]
    public void a_disposed_mysql_transaction_matches()
    {
        TransientMySqlFailure.Is(new ObjectDisposedException(nameof(MySqlTransaction))).ShouldBeTrue();
        TransientMySqlFailure.Is(new ObjectDisposedException("Something else")).ShouldBeFalse();
    }

    [Fact]
    public void unrelated_exceptions_do_not_match()
    {
        TransientMySqlFailure.Is(null).ShouldBeFalse();
        TransientMySqlFailure.Is(new InvalidOperationException()).ShouldBeFalse();
        TransientMySqlFailure.Is(new TimeoutException()).ShouldBeFalse();
    }

    [Fact]
    public void it_registers_one_rule_with_the_chosen_action()
    {
        var policies = new FailurePoliciesHolder(new FailureRuleCollection());

        policies.OnTransientMySqlFailure().ScheduleRetry(1.Seconds(), 5.Seconds());

        var rule = policies.Failures.ShouldHaveSingleItem();
        rule.Match.Matches(Transient()).ShouldBeTrue();
        rule.Match.Matches(Permanent()).ShouldBeFalse();
        rule.Count().ShouldBe(2);
    }

    // MySqlConnector's MySqlException constructors are internal
    private static MySqlException MySqlError(MySqlErrorCode code, Exception? inner = null)
    {
        var constructor = typeof(MySqlException).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(MySqlErrorCode), typeof(string), typeof(Exception)])!;

        return (MySqlException)constructor.Invoke([code, $"Error {code}", inner]);
    }

    private class FailurePoliciesHolder(FailureRuleCollection failures) : IWithFailurePolicies
    {
        public FailureRuleCollection Failures { get; } = failures;
    }
}
