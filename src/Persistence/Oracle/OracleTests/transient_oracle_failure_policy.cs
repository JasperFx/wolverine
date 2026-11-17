using System.Reflection;
using JasperFx.Core;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Oracle;

namespace OracleTests;

public class transient_oracle_failure_policy
{
    private static OracleException Transient() => OracleError(60);
    private static OracleException Permanent() => OracleError(1);

    [Theory]
    [InlineData(60)] // deadlock detected while waiting for resource
    [InlineData(54)] // resource busy and acquire with NOWAIT specified
    [InlineData(8177)] // can't serialize access for this transaction
    [InlineData(1089)] // immediate shutdown in progress
    [InlineData(3113)] // end-of-file on communication channel
    [InlineData(3114)] // not connected to ORACLE
    [InlineData(12170)] // connect timeout occurred
    [InlineData(12541)] // no listener
    public void transient_errors_match(int number)
    {
        TransientOracleFailure.Is(OracleError(number)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(1)] // unique constraint violated
    [InlineData(2291)] // integrity constraint violated, parent key not found
    [InlineData(1400)] // cannot insert NULL
    [InlineData(900)] // invalid SQL statement
    [InlineData(942)] // table or view does not exist
    public void permanent_errors_do_not_match(int number)
    {
        TransientOracleFailure.Is(OracleError(number)).ShouldBeFalse();
    }

    [Fact]
    public void wrapped_exceptions_are_unwrapped()
    {
        TransientOracleFailure.Is(new InvalidOperationException("outer", new Exception("middle", Transient())))
            .ShouldBeTrue();
        TransientOracleFailure.Is(new InvalidOperationException("outer", new Exception("middle", Permanent())))
            .ShouldBeFalse();
    }

    [Fact]
    public void unrelated_exceptions_do_not_match()
    {
        TransientOracleFailure.Is(null).ShouldBeFalse();
        TransientOracleFailure.Is(new InvalidOperationException()).ShouldBeFalse();
        TransientOracleFailure.Is(new TimeoutException()).ShouldBeFalse();
    }

    [Fact]
    public void it_registers_one_rule_with_the_chosen_action()
    {
        var policies = new FailurePoliciesHolder(new FailureRuleCollection());

        policies.OnTransientOracleFailure().ScheduleRetry(1.Seconds(), 5.Seconds());

        var rule = policies.Failures.ShouldHaveSingleItem();
        rule.Match.Matches(Transient()).ShouldBeTrue();
        rule.Match.Matches(Permanent()).ShouldBeFalse();
        rule.Count().ShouldBe(2);
    }

    // ODP.NET's OracleException constructors are internal
    private static OracleException OracleError(int number)
    {
        var constructor = typeof(OracleException).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(int), typeof(string), typeof(string), typeof(string), typeof(int)])!;

        return (OracleException)constructor.Invoke([number, "datasource", "", $"ORA-{number:00000}", 0]);
    }

    private class FailurePoliciesHolder(FailureRuleCollection failures) : IWithFailurePolicies
    {
        public FailureRuleCollection Failures { get; } = failures;
    }
}
