using IntegrationTests;
using JasperFx;
using Polecat;

[assembly: AssemblyFixture(typeof(Wolverine.Bobcat.Tests.PolecatPartitionObjects))]

namespace Wolverine.Bobcat.Tests;

/// <summary>
///     GH-5760. Provisions one Polecat store before any test class starts, so the partition function and scheme
///     every Polecat store in this database shares already exist, with a table on them, by the time the class
///     fixtures provision their own schemas in parallel.
/// </summary>
/// <remarks>
///     <para>
///     Polecat 6 partitions <c>pc_events</c> by <c>is_archived</c> by default, on <c>pf_pc_events_is_archived</c>
///     and <c>ps_pc_events_is_archived</c>. Those names are not schema-qualified because a SQL Server partition
///     function and scheme are database-scoped, so every Polecat fixture in this assembly uses the same two objects.
///     Polecat serializes migrations per schema only, so the fixtures still provision at the same time, and Weasel's
///     creation DDL for the two objects is a series of separate check-then-act statements: drop the scheme and the
///     function if no table is on them yet, then create each one if it is missing.
///     </para>
///     <para>
///     On a fresh database (every CI run) nothing is on them until the first fixture creates its <c>pc_events</c>,
///     so fixtures that started together could interleave those statements. One checked that no scheme used the
///     function, another created the scheme, and the first one's <c>DROP PARTITION FUNCTION</c> then failed with
///     "Partition function 'pf_pc_events_is_archived' is being used by one or more partition schemes". A fixture
///     reading the catalog while another one ran that DDL could also be chosen as a deadlock victim. Either way the
///     class fixture threw in <c>InitializeAsync</c> and every test in the class failed.
///     </para>
///     <para>
///     Once one table is on the scheme, that DDL drops nothing and creates nothing, so the fixtures no longer touch
///     the shared objects. This store is that table. Its schema is left in place for the rest of the run.
///     </para>
/// </remarks>
public sealed class PolecatPartitionObjects : IAsyncLifetime
{
    public const string SchemaName = "bobcat_partition_objects";

    public async ValueTask InitializeAsync()
    {
        await using var store = DocumentStore.For(opts =>
        {
            opts.ConnectionString = Servers.SqlServerConnectionString;
            opts.DatabaseSchemaName = SchemaName;
            opts.AutoCreateSchemaObjects = AutoCreate.None;
        });

        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
