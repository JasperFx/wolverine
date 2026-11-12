using Weasel.Core;
using Weasel.Postgresql.Tables;
using Weasel.Postgresql.Tables.Partitioning;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

/// <summary>
/// GH-5519. The schema delta of the PostgreSQL inbox table. Identical to Weasel's, except when a partitioned inbox goes
/// back to an unpartitioned one: <see cref="DurabilitySettings.EnableInboxPartitioning" /> set to false, or
/// <see cref="WolverineOptions.RestoreV6Defaults" /> called, after a Wolverine 7.0 start partitioned it.
/// </summary>
/// <remarks>
/// <para>Weasel writes the primary key change before its rebuild, and PostgreSQL refuses to give a partitioned table a
/// primary key without the partitioning column (0A000), so that migration failed and the host did not start. The
/// rebuild recreates the table with its primary key anyway, so this writes the rebuild alone.</para>
///
/// <para>It runs in an explicit transaction that first takes <c>ACCESS EXCLUSIVE</c> on the inbox, so the copy aside
/// sees every committed row and nothing reads or writes the inbox until the commit. (Going the other way the primary key
/// change takes that lock, see the migration guide.) PostgreSQL refuses LOCK TABLE in the implicit transaction a
/// pipelined script runs in, hence the explicit block.</para>
///
/// <para>A partitioned inbox can hold one message identity as both Incoming and Scheduled (and Handled), which the
/// unpartitioned primary key cannot. The copy back keeps the row that supersedes the others, as the scheduled message
/// poller would: Handled, then Incoming, then Scheduled.</para>
/// </remarks>
internal class IncomingEnvelopeTableDelta : TableDelta
{
    public IncomingEnvelopeTableDelta(Table expected, Table? actual) : base(expected, actual)
    {
    }

    /// <summary>
    /// True when this delta takes a partitioned inbox back to an unpartitioned one
    /// </summary>
    public bool UnpartitionsExistingTable =>
        Actual?.Partitioning != null && Expected.Partitioning == null && PartitionDelta == PartitionDelta.Rebuild;

    public override void WriteUpdate(Migrator rules, TextWriter writer)
    {
        if (!UnpartitionsExistingTable)
        {
            base.WriteUpdate(rules, writer);
            return;
        }

        var table = Expected.Identifier;
        var temp = new DbObjectName(table.Schema, table.Name + "_temp");

        // Columns the old table has too; a column only the new one has takes its default
        var columns = string.Join(", ", Expected.Columns.Select(x => x.Name)
            .Where(name => Actual!.Columns.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))));

        writer.WriteLine("begin;");
        writer.WriteLine($"lock table {table} in access exclusive mode;");
        writer.WriteLine($"create table {temp} as select * from {table};");
        writer.WriteLine($"drop table {table} cascade;");
        Expected.WriteCreateStatement(rules, writer);
        writer.WriteLine();
        writer.WriteLine($"insert into {table}({columns}) select {columns} from {temp} order by case {DatabaseConstants.Status} " +
                         $"when '{EnvelopeStatus.Handled}' then 0 when '{EnvelopeStatus.Incoming}' then 1 else 2 end " +
                         "on conflict do nothing;");
        writer.WriteLine($"drop table {temp} cascade;");
        writer.WriteLine("commit;");
    }
}
