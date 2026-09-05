using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

internal class OutgoingEnvelopeTable : Table
{
    public OutgoingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.OutgoingTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn<string>(DatabaseConstants.Destination).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.DeliverBy);
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();

        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);

        AddColumn<string>(DatabaseConstants.MessageType).NotNull();

        if (durability.OutboxStaleTime.HasValue)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Timestamp).DefaultValueByExpression("(now() at time zone 'utc')");
        }

        // GH-3971: the orphaned-message sweep asks `owner_id in (<dead owners>)`, worked out in memory
        // first, precisely so it can use this index. The predicate it replaced --
        // `owner_id <> 0 and owner_id not in (<live nodes>)` -- could not: the selective part is the
        // NOT IN, and everything else matches nearly every row in a healthy fleet, so it was a full scan
        // per database on every polling cycle. Partial because owner_id = 0 (unowned) is the one value
        // the sweep never asks for and, on a busy inbox, a large share of the rows.
        Indexes.Add(new IndexDefinition(PostgresqlIdentifier.Shorten($"idx_{DatabaseConstants.OutgoingTable}_owner"))
        {
            Columns = [DatabaseConstants.OwnerId],
            Predicate = $"{DatabaseConstants.OwnerId} <> 0"
        });

        // GH-4316: the outgoing recovery poll (`select distinct destination where owner_id = 0`)
        // and the per-destination recovery load ask for exactly the value the owner index above
        // excludes, so both were full scans every 5-second cycle. Partial on unowned rows so the
        // poll reads only what is actually recoverable.
        Indexes.Add(new IndexDefinition(PostgresqlIdentifier.Shorten($"idx_{DatabaseConstants.OutgoingTable}_recover"))
        {
            Columns = [DatabaseConstants.Destination],
            Predicate = $"{DatabaseConstants.OwnerId} = 0"
        });
    }
}