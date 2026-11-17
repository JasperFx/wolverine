using Marten.Exceptions;
using Npgsql;
using Wolverine.ErrorHandling;

namespace Wolverine.Marten;

/// <summary>
///     Recognizes failures that say something about the moment rather than about the message: the database was
///     unreachable, restarting or dropped the connection, and the same message is expected to succeed later.
///     Opt in with <see cref="TransientPostgresFailureExtensions.OnTransientPostgresFailure" />.
/// </summary>
public static class TransientPostgresFailure
{
    /// <summary>
    ///     True for a <see cref="NpgsqlException" /> that Npgsql reports as transient, a
    ///     <see cref="MartenCommandException" /> wrapping one, and the <see cref="ObjectDisposedException" /> on
    ///     the <see cref="NpgsqlTransaction" /> that a handler sees after its connection broke mid-transaction.
    ///     If the connection broke during the commit itself, the first attempt may have committed, so a retry
    ///     is at-least-once.
    /// </summary>
    public static bool Is(Exception exception) => exception switch
    {
        NpgsqlException npgsql => npgsql.IsTransient,
        MartenCommandException { InnerException: { } inner } => Is(inner),
        ObjectDisposedException { ObjectName: nameof(NpgsqlTransaction) } => true,
        _ => false
    };
}

public static class TransientPostgresFailureExtensions
{
    /// <summary>
    ///     Match transient PostgreSQL failures (see <see cref="TransientPostgresFailure.Is" />) and choose
    ///     what to do with them, like any other <c>OnException()</c> rule. Nothing is retried unless you
    ///     add an action. Rules are first match wins, so declare this before a broader rule that would also
    ///     match, such as one for <see cref="MartenCommandException" />.
    /// </summary>
    /// <example>
    ///     <code>
    ///     opts.Policies.OnTransientPostgresFailure()
    ///         .ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds());
    ///     </code>
    /// </example>
    public static PolicyExpression OnTransientPostgresFailure(this IWithFailurePolicies policies)
    {
        return policies.OnException(TransientPostgresFailure.Is, "Transient PostgreSQL failure");
    }
}
