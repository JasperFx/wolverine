using Weasel.SqlServer.Tables;

namespace Wolverine.SqlServer.Transport;

/// <summary>
///     GH-4862. The opt-in queue ordering column. SQL Server numbers every existing row when an
///     IDENTITY column is added to a populated table, so this NOT NULL column can be added in place
///     even though it declares no default -- Weasel's stock <see cref="TableColumn.CanAdd" /> would
///     otherwise call the table unpatchable and rebuild it. The existence guard makes concurrent
///     resource setup attempts safe for this additive migration.
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
        var qualifiedName = parent.Identifier.QualifiedName.Replace("'", "''");
        return
            $"if not exists (select 1 from sys.columns where object_id = object_id('{qualifiedName}') and name = '{Name}') alter table {parent.Identifier} add {ToDeclaration()};";
    }
}
