using Oracle.ManagedDataAccess.Client;
using Wolverine.ErrorHandling;

namespace Wolverine.Oracle;

/// <summary>
///     Recognizes Oracle failures that say something about the moment rather than about the message: a
///     deadlock, a busy resource, a serialization conflict, or a connection that broke, could not be made or
///     reached an instance that is starting or shutting down. The same message is expected to succeed later.
///     Opt in with <see cref="TransientOracleFailureExtensions.OnTransientOracleFailure" />.
/// </summary>
public static class TransientOracleFailure
{
    /// <summary>
    ///     The ORA- error numbers treated as transient: 51 (timeout waiting for a resource), 54 and 30006
    ///     (resource busy), 60 (deadlock), 2049 (distributed transaction lock timeout), 8177 (cannot serialize
    ///     access), 1033, 1034 and 1089 (instance starting, unavailable or shutting down), 3113, 3114 and 3135
    ///     (connection lost), 12170, 12514, 12528, 12537, 12541, 12543 and 12571 (connection could not be made
    ///     or was closed) and 25408 (call cannot be safely replayed).
    /// </summary>
    public static IReadOnlySet<int> ErrorNumbers { get; } = new HashSet<int>
    {
        51, 54, 60, 1033, 1034, 1089, 2049, 3113, 3114, 3135, 8177, 12170, 12514, 12528, 12537, 12541,
        12543, 12571, 25408, 30006
    };

    /// <summary>
    ///     True for an <see cref="OracleException" /> that ODP.NET reports as transient or recoverable, or that
    ///     carries one of the <see cref="ErrorNumbers" />. Wrapper exceptions, such as EF Core's, are unwrapped
    ///     through <see cref="Exception.InnerException" />. Constraint violations, syntax errors and other
    ///     permanent failures do not match.
    /// </summary>
    public static bool Is(Exception? exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (e is OracleException oracle && isTransient(oracle)) return true;
        }

        return false;
    }

    private static bool isTransient(OracleException oracle)
    {
        if (oracle.IsTransient || oracle.IsRecoverable || ErrorNumbers.Contains(oracle.Number)) return true;

        foreach (OracleError error in oracle.Errors)
        {
            if (ErrorNumbers.Contains(error.Number)) return true;
        }

        return false;
    }
}

public static class TransientOracleFailureExtensions
{
    /// <summary>
    ///     Match transient Oracle failures (see <see cref="TransientOracleFailure.Is" />) and choose what to do
    ///     with them, like any other <c>OnException()</c> rule. Nothing is retried unless you add an action.
    ///     Rules are first match wins, so declare this before a broader rule that would also match, such as one
    ///     for <see cref="OracleException" />.
    /// </summary>
    /// <example>
    ///     <code>
    ///     opts.Policies.OnTransientOracleFailure()
    ///         .ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds());
    ///     </code>
    /// </example>
    public static PolicyExpression OnTransientOracleFailure(this IWithFailurePolicies policies)
    {
        return policies.OnException(TransientOracleFailure.Is, "Transient Oracle failure");
    }
}
