using Microsoft.Data.Sqlite;
using Wolverine.ErrorHandling;

namespace Wolverine.Sqlite;

/// <summary>
///     Recognizes SQLite failures that say something about the moment rather than about the message: the
///     database file was busy or a table was locked by another connection. The same message is expected to
///     succeed later. Opt in with <see cref="TransientSqliteFailureExtensions.OnTransientSqliteFailure" />.
/// </summary>
public static class TransientSqliteFailure
{
    /// <summary>
    ///     SQLITE_BUSY
    /// </summary>
    public const int Busy = 5;

    /// <summary>
    ///     SQLITE_LOCKED
    /// </summary>
    public const int Locked = 6;

    /// <summary>
    ///     True for a <see cref="SqliteException" /> with SQLITE_BUSY (5) or SQLITE_LOCKED (6), including their
    ///     extended codes such as SQLITE_BUSY_SNAPSHOT or SQLITE_LOCKED_SHAREDCACHE. Wrapper exceptions, such as
    ///     the ones Fisher and EF Core throw, are unwrapped through <see cref="Exception.InnerException" />.
    ///     Constraint violations, syntax errors and other permanent failures do not match.
    /// </summary>
    public static bool Is(Exception? exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (e is SqliteException sqlite && (isBusyOrLocked(sqlite.SqliteErrorCode) ||
                                                isBusyOrLocked(sqlite.SqliteExtendedErrorCode)))
            {
                return true;
            }
        }

        return false;
    }

    // An extended result code keeps the primary code in its low byte
    private static bool isBusyOrLocked(int code) => (code & 0xFF) is Busy or Locked;
}

public static class TransientSqliteFailureExtensions
{
    /// <summary>
    ///     Match transient SQLite failures (see <see cref="TransientSqliteFailure.Is" />) and choose what to do
    ///     with them, like any other <c>OnException()</c> rule. Nothing is retried unless you add an action.
    ///     Rules are first match wins, so declare this before a broader rule that would also match, such as one
    ///     for <see cref="SqliteException" />.
    /// </summary>
    /// <example>
    ///     <code>
    ///     opts.Policies.OnTransientSqliteFailure()
    ///         .ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds());
    ///     </code>
    /// </example>
    public static PolicyExpression OnTransientSqliteFailure(this IWithFailurePolicies policies)
    {
        return policies.OnException(TransientSqliteFailure.Is, "Transient SQLite failure");
    }
}
