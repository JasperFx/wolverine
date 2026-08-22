namespace Wolverine.RDBMS;

/// <summary>
/// Thrown when a DDL statement in Wolverine's own database migration fails. See GH-3997: these failures
/// used to be logged and swallowed, so the host started up against storage that had never been created
/// and died much later against an error that named nothing recognizable.
/// </summary>
public class WolverineSchemaException : Exception
{
    public WolverineSchemaException(string sql, Exception innerException) : base(
        $"Error while applying a Wolverine database migration:\n\n{sql}\n\n{innerException.Message}", innerException)
    {
        Sql = sql;
    }

    /// <summary>
    /// The DDL that failed
    /// </summary>
    public string Sql { get; }
}
