using JasperFx.Core;
using Weasel.Core;
using Weasel.SqlServer.Tables;

namespace Wolverine.SqlServer.Transport;

/// <summary>
///     GH-4989. Moves an existing queue table between the default layout (clustered primary key on
///     <c>id</c>) and the <c>OptimizeQueueThroughput()</c> layout (clustered <c>seq</c> identity, unique
///     index on <c>id</c>) by rebuilding it, inside one transaction, with its messages kept in queue order.
/// </summary>
/// <remarks>
///     An in-place migration cannot do this. A new identity column numbers the existing rows in physical
///     order, which for the default layout is the order of the random message ids, so the queue would be
///     dequeued out of order afterwards. The two layouts also cannot both hold their identity at once
///     (<c>seq</c>, and <c>queue_order</c> under <c>EnableDeterministicQueueOrdering()</c>), and the default
///     layout's add-only migrations keep everything it does not declare. Copying the rows out, recreating
///     the table and inserting them back <c>ORDER BY</c> queue order handles all three: SQL Server assigns
///     identity values in the order of an <c>INSERT ... SELECT ... ORDER BY</c>.
/// </remarks>
internal sealed class QueueTableLayoutSwitchDelta : TableDelta
{
    internal const string SeqColumn = "seq";

    private readonly string _orderingColumn;

    public QueueTableLayoutSwitchDelta(Table expected, Table actual, string orderingColumn) : base(expected, actual)
    {
        _orderingColumn = orderingColumn;
    }

    /// <summary>
    ///     Is the existing table on the other layout? The <c>seq</c> identity only exists on the
    ///     <c>OptimizeQueueThroughput()</c> layout.
    /// </summary>
    internal static bool IsLayoutSwitch(Table expected, Table? actual)
    {
        return actual != null && expected.HasColumn(SeqColumn) != actual.HasColumn(SeqColumn);
    }

    protected override SchemaPatchDifference compare(Table expected, Table? actual)
    {
        // Run the standard comparison for its column, index and key deltas, but the rebuild below applies
        // whatever it would have refused as an in-place change
        var difference = base.compare(expected, actual);
        return actual == null ? difference : SchemaPatchDifference.Update;
    }

    public override void WriteUpdate(Migrator rules, TextWriter writer)
    {
        writeRebuild(rules, writer, Actual!, Expected);
    }

    public override void WriteRollback(Migrator rules, TextWriter writer)
    {
        writeRebuild(rules, writer, Expected, Actual!);
    }

    private void writeRebuild(Migrator rules, TextWriter writer, Table from, Table to)
    {
        var table = Expected.Identifier;
        var temp = $"#wolverine_layout_switch_{table.Name}";

        // Every column the new layout keeps, except its identity, which the insert assigns in order
        var copied = to.Columns
            .Where(x => !x.IsAutoNumber && from.HasColumn(x.Name))
            .Select(x => quote(x.Name))
            .ToArray();

        // Queue order on the old layout. A ready table on the OptimizeQueueThroughput() layout dequeues by
        // seq alone, so seq is its order; otherwise the ordering column, then the identity as the
        // tie-breaker when there is one
        var tieBreaker = from.Columns.FirstOrDefault(x => x.IsAutoNumber);
        var ordering = tieBreaker != null && tieBreaker.Name == SeqColumn && _orderingColumn == "timestamp"
            ? new List<string> { quote(SeqColumn) }
            : new List<string> { quote(_orderingColumn) };
        if (tieBreaker != null && !ordering.Contains(quote(tieBreaker.Name))) ordering.Add(quote(tieBreaker.Name));

        var saved = copied.Concat(tieBreaker == null ? [] : [quote(tieBreaker.Name)]).Join(", ");

        writer.WriteLine("SET XACT_ABORT ON;");
        writer.WriteLine("BEGIN TRANSACTION;");
        writer.WriteLine($"IF OBJECT_ID('tempdb..{temp}') IS NOT NULL DROP TABLE {temp};");
        writer.WriteLine($"SELECT {saved} INTO {temp} FROM {table} WITH (TABLOCKX, HOLDLOCK);");
        writer.WriteLine($"DROP TABLE {table};");

        to.WriteCreateStatement(rules, writer);

        writer.WriteLine(
            $"INSERT INTO {table} ({copied.Join(", ")}) SELECT {copied.Join(", ")} FROM {temp} ORDER BY {ordering.Join(", ")};");
        writer.WriteLine($"DROP TABLE {temp};");
        writer.WriteLine("COMMIT TRANSACTION;");
    }

    private static string quote(string name) => $"[{name}]";
}
