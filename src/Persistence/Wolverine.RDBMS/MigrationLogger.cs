using System.Data.Common;
using Microsoft.Extensions.Logging;
using Weasel.Core.Migrations;

namespace Wolverine.RDBMS;

public class MigrationLogger : IMigrationLogger
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

    /// <summary>
    /// Weasel hands a failed DDL statement here instead of throwing, because a non-default
    /// <see cref="IMigrationLogger"/> is taken to mean the caller wants to decide. Wolverine's decision is
    /// to fail — matching Marten, whose logger throws a <c>MartenSchemaException</c>.
    /// </summary>
    /// <remarks>
    /// GH-3997: this used to only log. A misconfigured schema name meant every CREATE failed, each one
    /// logged as an error, and startup carried on regardless — dying later inside
    /// <c>LoadNodeAgentStateAsync</c> against an error that named nothing the user had configured.
    /// <para>
    /// The two paths that could legitimately see a failure here are both already covered:
    /// <see cref="MessageDatabase{T}.MigrateAsync(JasperFx.AutoCreate?)" /> retries the whole migration once
    /// after a short delay, which absorbs a DDL race with another node starting at the same instant, and
    /// <c>ResourceMigrationFailureMode.ContinueOnFailures</c> lets a host that would rather start against
    /// not-yet-migrated storage keep the old behavior.
    /// </para>
    /// </remarks>
    public void OnFailure(DbCommand command, Exception ex)
    {
        _logger.LogError(ex, "Error executing Wolverine Envelope Storage database migration: {Sql}",
            command.CommandText);

        throw new WolverineSchemaException(command.CommandText, ex);
    }
}
