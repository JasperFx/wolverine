
using JasperFx.Core;

namespace Wolverine.Postgresql;

public static class PostgresqlConfigurationExtensions
{
    /// <summary>
    ///     Register Postgresql backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schema"></param>
    public static void PersistMessagesWithPostgresql(this WolverineOptions options, string connectionString,
        string? schema = null)
    {
        options.Include<PostgresqlBackedPersistence>(o =>
        {
            o.Settings.ConnectionString = connectionString;
            if (schema.IsNotEmpty())
            {
                o.Settings.SchemaName = schema;
            }
        });
    }
}