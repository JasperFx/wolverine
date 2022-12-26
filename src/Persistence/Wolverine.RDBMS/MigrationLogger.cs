using System.Data.Common;
using Microsoft.Extensions.Logging;
using Weasel.Core.Migrations;

namespace Wolverine.RDBMS;

internal class MigrationLogger : IMigrationLogger
{
    private readonly ILogger _logger;

    public MigrationLogger(ILogger logger)
    {
        _logger = logger;
    }

    public void SchemaChange(string sql)
    {
        _logger.LogInformation("Applied database migration for Wolverine Envelope Storage: {Sql}", sql);
    }

    public void OnFailure(DbCommand command, Exception ex)
    {
        _logger.LogError(ex, "Error executing Wolverine Envelope Storage database migration: {Sql}",
            command.CommandText);
    }
}