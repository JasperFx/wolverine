namespace Wolverine.RDBMS;

/// <summary>
/// Folds a configured "schema name" into a table name for database engines that have no schemas.
/// See <see cref="DatabaseSettings.SchemaNameIsTablePrefix"/> and GH-3943.
/// </summary>
public static class TablePrefixing
{
    /// <summary>
    /// The one schema name a plain SQLite connection always knows. It is both Wolverine.Sqlite's
    /// default and the "no prefix at all" value, so hosts that never set a schema name — and every
    /// database they have already provisioned — keep the bare <c>wolverine_*</c> table names.
    /// </summary>
    public const string DefaultSqliteSchemaName = "main";

    /// <summary>
    /// Renders <paramref name="tableName"/> prefixed by <paramref name="schemaName"/>. An empty
    /// schema name, or the default <c>main</c>, yields the table name unchanged.
    /// </summary>
    public static string Apply(string? schemaName, string tableName)
    {
        if (string.IsNullOrEmpty(schemaName)) return tableName;
        if (schemaName == DefaultSqliteSchemaName) return tableName;

        return $"{schemaName}_{tableName}";
    }
}
