using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

/// <summary>
/// GH-4742. Claims for <c>[DeduplicatedWithResponse]</c> HTTP endpoints. Provisioned only when
/// <see cref="DurabilitySettings.EnableDeduplicatedResponses" /> is set. The primary key arbitrates claims as
/// it does on <see cref="DeduplicationTable" />.
/// </summary>
internal class DeduplicatedResponsesTable : Table
{
    public DeduplicatedResponsesTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.DeduplicatedResponsesTableName))
    {
        AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull().AsPrimaryKey();
        AddColumn<DateTimeOffset>(DatabaseConstants.Expires).NotNull();
        AddColumn(DatabaseConstants.Fingerprint, "varchar(64)").NotNull();

        // Null until answered. Text, not jsonb, so a replay is byte-identical.
        AddColumn(DatabaseConstants.ResponseStatusCode, "integer");
        AddColumn(DatabaseConstants.ResponseBody, "text");
        AddColumn(DatabaseConstants.ResponseLocation, "text");

        Indexes.Add(new IndexDefinition(
            PostgresqlIdentifier.Shorten($"idx_{DatabaseConstants.DeduplicatedResponsesTableName}_expires"))
        {
            Columns = [DatabaseConstants.Expires]
        });
    }
}
