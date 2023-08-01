using JasperFx.Core;

namespace Wolverine.SqlServer;

public static class SqlServerConfigurationExtensions
{
    /// <summary>
    ///     Register sql server backed message persistence to a known connection string
    /// </summary>
    /// <param name="settings"></param>
    /// <param name="connectionString"></param>
    /// <param name="schema"></param>
    public static void PersistMessagesWithSqlServer(this WolverineOptions options, string connectionString,
        string? schema = null)
    {
        options.Include<SqlServerBackedPersistence>(x =>
        {
            x.Settings.ConnectionString = connectionString;

            if (schema.IsNotEmpty())
            {
                x.Settings.SchemaName = schema;
            }
            else
            {
                schema = "dbo";
                
            }

            x.Settings.ScheduledJobLockId = $"{schema}:scheduled-jobs".GetDeterministicHashCode();
        });
    }
}