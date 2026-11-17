using System.Reflection;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.SqlServer;
using Xunit;

namespace SqlServerTests;

public class transient_sql_server_failure_policy
{
    // 1205 = chosen as the deadlock victim
    private static SqlException Transient() => SqlServerError(1205);

    // 2627 = violation of a unique constraint
    private static SqlException Permanent() => SqlServerError(2627);

    [Theory]
    [InlineData(-2)] // timeout
    [InlineData(233)] // no process on the other end of the pipe
    [InlineData(1205)] // deadlock victim
    [InlineData(10054)] // connection reset by the server
    [InlineData(40197)] // Azure SQL: error processing the request, retry
    [InlineData(40501)] // Azure SQL: service busy
    [InlineData(40613)] // Azure SQL: database not currently available
    [InlineData(49918)] // Azure SQL: not enough resources
    public void transient_errors_match(int number)
    {
        TransientSqlServerFailure.Is(SqlServerError(number)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(2627)] // unique constraint violation
    [InlineData(2601)] // duplicate key in a unique index
    [InlineData(547)] // foreign key / check constraint violation
    [InlineData(102)] // incorrect syntax
    [InlineData(208)] // invalid object name
    public void permanent_errors_do_not_match(int number)
    {
        TransientSqlServerFailure.Is(SqlServerError(number)).ShouldBeFalse();
    }

    [Fact]
    public void any_transient_error_in_the_collection_matches()
    {
        TransientSqlServerFailure.Is(SqlServerError(3621, 1205)).ShouldBeTrue();
        TransientSqlServerFailure.Is(SqlServerError(3621, 2627)).ShouldBeFalse();
    }

    [Fact]
    public void wrapped_exceptions_are_unwrapped()
    {
        // Like EF Core's DbUpdateException or a Polecat exception wrapping the SqlException, at any depth
        TransientSqlServerFailure.Is(new InvalidOperationException("outer", new Exception("middle", Transient())))
            .ShouldBeTrue();
        TransientSqlServerFailure.Is(new InvalidOperationException("outer", new Exception("middle", Permanent())))
            .ShouldBeFalse();
    }

    [Fact]
    public void a_disposed_sql_transaction_matches()
    {
        TransientSqlServerFailure.Is(new ObjectDisposedException(nameof(SqlTransaction))).ShouldBeTrue();
        TransientSqlServerFailure.Is(new ObjectDisposedException("Something else")).ShouldBeFalse();
    }

    [Fact]
    public void unrelated_exceptions_do_not_match()
    {
        TransientSqlServerFailure.Is(null).ShouldBeFalse();
        TransientSqlServerFailure.Is(new InvalidOperationException()).ShouldBeFalse();
        TransientSqlServerFailure.Is(new TimeoutException()).ShouldBeFalse();
    }

    [Fact]
    public void it_registers_one_rule_with_the_chosen_action()
    {
        var policies = new FailurePoliciesHolder(new FailureRuleCollection());

        policies.OnTransientSqlServerFailure().ScheduleRetry(1.Seconds(), 5.Seconds());

        var rule = policies.Failures.ShouldHaveSingleItem();
        rule.Match.Matches(Transient()).ShouldBeTrue();
        rule.Match.Matches(Permanent()).ShouldBeFalse();
        rule.Count().ShouldBe(2);
    }

    // SqlClient has no public way to build a SqlException, so this goes through the same internal
    // factory SqlClient uses when the server reports errors
    private static SqlException SqlServerError(params int[] numbers)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;

        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
        var add = typeof(SqlErrorCollection).GetMethod("Add", flags)!;
        var errorConstructor = typeof(SqlError).GetConstructor(flags,
            [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)])!;

        foreach (var number in numbers)
        {
            var error = errorConstructor.Invoke([number, (byte)1, (byte)16, "server", $"Error {number}", "", 0, null]);
            add.Invoke(errors, [error]);
        }

        var create = typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(SqlErrorCollection), typeof(string)])!;

        return (SqlException)create.Invoke(null, [errors, "16.00.0000"])!;
    }

    private class FailurePoliciesHolder(FailureRuleCollection failures) : IWithFailurePolicies
    {
        public FailureRuleCollection Failures { get; } = failures;
    }
}
