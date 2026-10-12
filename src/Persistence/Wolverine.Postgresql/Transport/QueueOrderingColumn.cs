using Weasel.Postgresql.Tables;

namespace Wolverine.Postgresql.Transport;

/// <summary>
///     The opt-in queue ordering column can be added to existing populated tables because PostgreSQL
///     assigns an identity value to each existing row. IF NOT EXISTS also makes concurrent resource
///     setup attempts safe for this additive migration.
/// </summary>
internal sealed class QueueOrderingColumn : TableColumn
{
    public QueueOrderingColumn() : base(QueueTable.QueueOrderColumn, "bigint")
    {
        AllowNulls = false;
        IsAutoNumber = true;
    }

    public override bool CanAdd() => true;

    public override string AddColumnSql(Table parent)
    {
        return $"alter table {parent.Identifier} add column if not exists {ToDeclaration()};";
    }
}
