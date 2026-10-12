using JasperFx.Core;
using Weasel.Core;
using Weasel.MySql;
using Weasel.MySql.Tables;

namespace Wolverine.MySql.Transport;

/// <summary>
///     GH-4862. The opt-in tie-breaker for the MySQL queue tables: a persistent
///     <c>queue_order BIGINT AUTO_INCREMENT</c> column, the unique key MySQL requires for an
///     AUTO_INCREMENT column, and a composite index for the dequeue / promotion ordering.
/// </summary>
internal static class QueueOrdering
{
    internal const string QueueOrderColumn = "queue_order";

    /// <summary>
    ///     Adds the tie-breaker column, its unique key and the composite ordering index to
    ///     <paramref name="table" />. The legacy indexes are left exactly as they were.
    /// </summary>
    internal static void AddTo(Table table, string tableName, string orderingColumn)
    {
        table.AddColumn(QueueOrderColumn, "BIGINT").NotNull().AutoIncrement();

        // MySQL only accepts an AUTO_INCREMENT column that leads a key. Weasel writes this key inline
        // in CREATE TABLE, and QueueOrderingTableDelta adds it in the same ALTER TABLE as the column.
        table.Indexes.Add(new IndexDefinition(UniqueKeyName(tableName))
        {
            Columns = [QueueOrderColumn],
            IsUnique = true
        });

        table.Indexes.Add(new IndexDefinition(MySqlIdentifier.Shorten($"idx_{tableName}_{orderingColumn}_{QueueOrderColumn}"))
        {
            Columns = [orderingColumn, QueueOrderColumn]
        });
    }

    internal static string UniqueKeyName(string tableName) =>
        MySqlIdentifier.Shorten($"uq_{tableName}_{QueueOrderColumn}");
}

/// <summary>
///     Weasel adds a missing column and a missing index in separate statements. MySQL rejects
///     <c>ADD COLUMN ... AUTO_INCREMENT</c> on its own (error 1075, the column must be defined as a
///     key), so when the tie-breaker column is missing this delta adds the column and its unique key
///     in a single <c>ALTER TABLE</c>. On a populated table that one statement numbers every existing
///     row. Everything else is left to the standard MySQL table delta.
/// </summary>
internal sealed class QueueOrderingTableDelta : TableDelta
{
    private readonly string _uniqueKeyName;

    public QueueOrderingTableDelta(Table expected, Table? actual, string uniqueKeyName) : base(expected, actual)
    {
        _uniqueKeyName = uniqueKeyName;
    }

    public override void WriteUpdate(Migrator migrator, TextWriter writer)
    {
        if (Difference != SchemaPatchDifference.Update)
        {
            base.WriteUpdate(migrator, writer);
            return;
        }

        withoutQueueOrderAddition((column, key) =>
        {
            var keyName = SchemaUtils.QuoteName(key?.Name ?? _uniqueKeyName);
            writer.WriteLine(
                $"ALTER TABLE {Expected.Identifier.QualifiedName} ADD COLUMN {column.ToDeclaration()}, ADD UNIQUE KEY {keyName} ({SchemaUtils.QuoteName(QueueOrdering.QueueOrderColumn)});");
        }, () => base.WriteUpdate(migrator, writer));
    }

    public override void WriteRollback(Migrator migrator, TextWriter writer)
    {
        if (Actual == null)
        {
            base.WriteRollback(migrator, writer);
            return;
        }

        // Dropping the column drops its unique key with it, so the key must not be dropped separately.
        withoutQueueOrderAddition((_, _) =>
        {
            writer.WriteLine(
                $"ALTER TABLE {Expected.Identifier.QualifiedName} DROP COLUMN {SchemaUtils.QuoteName(QueueOrdering.QueueOrderColumn)};");
        }, () => base.WriteRollback(migrator, writer));
    }

    private void withoutQueueOrderAddition(Action<TableColumn, IndexDefinition?> writeCombined, Action writeRest)
    {
        var column = Columns?.Missing.FirstOrDefault(x => x.Name.EqualsIgnoreCase(QueueOrdering.QueueOrderColumn));
        if (column == null)
        {
            writeRest();
            return;
        }

        var columnIndex = Columns!.Missing.IndexOf(column);
        var key = Indexes?.Missing.FirstOrDefault(x => x.Name.EqualsIgnoreCase(_uniqueKeyName));
        var keyIndex = key == null ? -1 : Indexes!.Missing.IndexOf(key);

        writeCombined(column, key);

        // Take both out of the standard delta only while it writes, so this delta stays reusable.
        Columns.Missing.RemoveAt(columnIndex);
        if (key != null) Indexes!.Missing.RemoveAt(keyIndex);
        try
        {
            writeRest();
        }
        finally
        {
            Columns.Missing.Insert(columnIndex, column);
            if (key != null) Indexes!.Missing.Insert(keyIndex, key);
        }
    }
}
