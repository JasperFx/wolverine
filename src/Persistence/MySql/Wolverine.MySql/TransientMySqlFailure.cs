using System.Net.Sockets;
using MySqlConnector;
using Wolverine.ErrorHandling;

namespace Wolverine.MySql;

/// <summary>
///     Recognizes MySQL failures that say something about the moment rather than about the message: a
///     deadlock, a lock wait timeout, a dropped or refused connection, or the server shutting down. The same
///     message is expected to succeed later. Opt in with
///     <see cref="TransientMySqlFailureExtensions.OnTransientMySqlFailure" />.
/// </summary>
public static class TransientMySqlFailure
{
    /// <summary>
    ///     The error codes treated as transient on top of the ones MySqlConnector already reports through
    ///     <see cref="MySqlException.IsTransient" /> (too many connections, unable to connect, lock wait timeout
    ///     and deadlocks): the server shutting down, aborted connections, the server closing an idle connection
    ///     and a command timeout.
    /// </summary>
    public static IReadOnlySet<MySqlErrorCode> ErrorCodes { get; } = new HashSet<MySqlErrorCode>
    {
        MySqlErrorCode.LockDeadlock,
        MySqlErrorCode.LockWaitTimeout,
        MySqlErrorCode.ServerShutdown,
        MySqlErrorCode.AbortingConnection,
        MySqlErrorCode.NewAbortingConnection,
        MySqlErrorCode.ClientInteractionTimeout,
        MySqlErrorCode.CommandTimeoutExpired
    };

    /// <summary>
    ///     True for a <see cref="MySqlException" /> that MySqlConnector reports as transient, that carries one of
    ///     the <see cref="ErrorCodes" />, or that was caused by the connection breaking (an
    ///     <see cref="IOException" /> or <see cref="SocketException" /> inside it), and for the
    ///     <see cref="ObjectDisposedException" /> on <see cref="MySqlTransaction" /> a handler sees after its
    ///     connection broke mid-transaction. Wrapper exceptions, such as EF Core's, are unwrapped through
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

    private static bool isTransient(Exception exception) => exception switch
    {
        MySqlException mysql => mysql.IsTransient
                                || ErrorCodes.Contains(mysql.ErrorCode)
                                || mysql.InnerException is IOException or SocketException,
        ObjectDisposedException { ObjectName: nameof(MySqlTransaction) } => true,
        _ => false
    };
}

public static class TransientMySqlFailureExtensions
{
    /// <summary>
    ///     Match transient MySQL failures (see <see cref="TransientMySqlFailure.Is" />) and choose what to do
    ///     with them, like any other <c>OnException()</c> rule. Nothing is retried unless you add an action.
    ///     Rules are first match wins, so declare this before a broader rule that would also match, such as one
    ///     for <see cref="MySqlException" />.
    /// </summary>
    /// <example>
    ///     <code>
    ///     opts.Policies.OnTransientMySqlFailure()
    ///         .ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds());
    ///     </code>
    /// </example>
    public static PolicyExpression OnTransientMySqlFailure(this IWithFailurePolicies policies)
    {
        return policies.OnException(TransientMySqlFailure.Is, "Transient MySQL failure");
    }
}
