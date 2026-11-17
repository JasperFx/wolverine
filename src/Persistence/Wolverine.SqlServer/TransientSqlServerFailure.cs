using Microsoft.Data.SqlClient;
using Wolverine.ErrorHandling;

namespace Wolverine.SqlServer;

/// <summary>
///     Recognizes SQL Server failures that say something about the moment rather than about the message: a
///     deadlock, a lock timeout, a dropped or refused connection, or Azure SQL being busy, failing over or
///     throttling. The same message is expected to succeed later. Opt in with
///     <see cref="TransientSqlServerFailureExtensions.OnTransientSqlServerFailure" />.
/// </summary>
public static class TransientSqlServerFailure
{
    /// <summary>
    ///     The SQL Server error numbers treated as transient: -2 (client timeout), 20, 64, 121, 233, 10053, 10054
    ///     and 10060 (the connection broke or could not be made), 1205 (deadlock victim), 1222 (lock request
    ///     timeout), 4221, 10928, 10929, 40143, 40197, 40501, 40613, 49918, 49919 and 49920 (Azure SQL busy,
    ///     failing over or throttling) and 41301, 41302, 41305, 41325 and 41839 (In-Memory OLTP conflicts).
    /// </summary>
    public static IReadOnlySet<int> ErrorNumbers { get; } = new HashSet<int>
    {
        -2, 20, 64, 121, 233, 1205, 1222, 4221, 10053, 10054, 10060, 10928, 10929,
        40143, 40197, 40501, 40613, 41301, 41302, 41305, 41325, 41839, 49918, 49919, 49920
    };

    /// <summary>
    ///     True for a <see cref="SqlException" /> that SqlClient reports as transient or that carries one of the
    ///     <see cref="ErrorNumbers" />, and for the <see cref="ObjectDisposedException" /> on
    ///     <see cref="SqlTransaction" /> a handler sees after its connection broke mid-transaction. Wrapper
    ///     exceptions, such as the ones Polecat and EF Core throw, are unwrapped through
    ///     <see cref="Exception.InnerException" />. Constraint violations, syntax errors and other permanent
    ///     failures do not match.
    /// </summary>
    public static bool Is(Exception? exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (isTransient(e)) return true;
        }

        return false;
    }

    private static bool isTransient(Exception exception)
    {
        switch (exception)
        {
            case SqlException sql:
                if (sql.IsTransient || ErrorNumbers.Contains(sql.Number)) return true;

                foreach (SqlError error in sql.Errors)
                {
                    if (ErrorNumbers.Contains(error.Number)) return true;
                }

                return false;

            case ObjectDisposedException { ObjectName: nameof(SqlTransaction) }:
                return true;

            default:
                return false;
        }
    }
}

public static class TransientSqlServerFailureExtensions
{
    /// <summary>
    ///     Match transient SQL Server failures (see <see cref="TransientSqlServerFailure.Is" />) and choose
    ///     what to do with them, like any other <c>OnException()</c> rule. Nothing is retried unless you
    ///     add an action. Rules are first match wins, so declare this before a broader rule that would also
    ///     match, such as one for <see cref="SqlException" />.
    /// </summary>
    /// <example>
    ///     <code>
    ///     opts.Policies.OnTransientSqlServerFailure()
    ///         .ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds());
    ///     </code>
    /// </example>
    public static PolicyExpression OnTransientSqlServerFailure(this IWithFailurePolicies policies)
    {
        return policies.OnException(TransientSqlServerFailure.Is, "Transient SQL Server failure");
    }
}
