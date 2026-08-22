namespace Wolverine.RDBMS;

/// <summary>
/// Guards the schema names Wolverine is given for its message storage and database backed transports.
/// See GH-3997.
/// </summary>
/// <remarks>
/// Wolverine renders every one of its tables as <c>{schema}.{table}</c> with no delimiting -- both in the
/// hand written SQL (<see cref="DatabaseSettings.TableNameFor"/>) and in the Weasel generated DDL, where
/// <c>IDatabaseProvider.ToQualifiedName</c> is a plain concatenation. A "schema name" that is itself
/// multi-part therefore produces a name with more parts than any of these engines accept -- SQL Server
/// reports <c>The object name '...' contains more than the maximum number of prefixes. The maximum is 2</c>,
/// and PostgreSQL reports <c>improper qualified name (too many dotted names)</c>. Weasel's
/// <c>CREATE SCHEMA</c> is the one statement that delimits the name, so the schema itself is created and
/// only the tables fail, which is exactly as confusing as it sounds. Catching this where the name is
/// configured is far kinder than the failure it produces otherwise.
/// </remarks>
public static class SchemaNameValidation
{
    /// <summary>
    /// Throws if <paramref name="schemaName"/> is a multi-part name rather than a single database
    /// identifier.
    /// </summary>
    /// <remarks>
    /// A name the caller has already delimited -- <c>[crm.sales]</c> -- is rejected along with the bare
    /// form, even though Weasel passes a pre-delimited identifier through to the table DDL untouched.
    /// Delimiting gets you a host that starts exactly once: Weasel's <c>CREATE SCHEMA</c> guard compares
    /// <c>sys.schemas.name</c> against the spelling it was handed, brackets and all, so the guard never
    /// matches and every subsequent start re-issues the <c>CREATE SCHEMA</c> against a schema that now
    /// exists. Schema difference detection cannot match a delimited name against the catalog either, so
    /// the store re-applies its whole DDL every time and never picks up a later column-level change.
    /// </remarks>
    public static void AssertValid(string? schemaName, string parameterName)
    {
        if (schemaName == null) return;
        if (!schemaName.Contains('.')) return;

        throw new ArgumentOutOfRangeException(parameterName, schemaName,
            $"'{schemaName}' is not a usable database schema name for Wolverine. A schema name has to be a single database identifier, but this one is multi-part -- Wolverine would emit '{schemaName}.wolverine_incoming_envelopes', and no supported database engine accepts a name with that many parts. Did you mean '{schemaName.Split('.')[0]}', with the database itself chosen by the connection string? Delimiting the name does not help: the schema would be created, but every restart afterwards fails.");
    }
}
