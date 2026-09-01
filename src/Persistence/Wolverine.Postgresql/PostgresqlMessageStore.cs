using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql.Schema;
using Wolverine.Postgresql.Util;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using CascadeAction = Weasel.Postgresql.CascadeAction;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;
using Table = Weasel.Postgresql.Tables.Table;

namespace Wolverine.Postgresql;

internal class PostgresqlMessageStore : MessageDatabase<NpgsqlConnection>, IConnectionBudgetProbe
{
    /// <summary>
    /// Row-count threshold below which <see cref="FetchCountsAsync"/> abandons the cheap
    /// <c>pg_class.reltuples</c> estimate for the outgoing and dead letter tables and issues a
    /// literal <c>select count(*)</c> instead. See GH-3885.
    /// <para>
    /// The trade-off: PostgreSQL's autoanalyze only fires after roughly
    /// <c>50 + 0.1 * reltuples</c> tuples have changed, so a *small* table effectively never
    /// re-analyzes and its estimate freezes at whatever value it had when the statistics were
    /// last collected -- a quiet dead letter table holding 42 rows will report a stale 40 forever.
    /// An exact count on a small table is trivially cheap, so below this threshold we simply pay
    /// for the truth. Above it, the estimate is both accurate enough (autoanalyze fires regularly
    /// once the table is large) and the only affordable option, since a sequential scan over a
    /// large outgoing table would be far too expensive to run on every metrics sample.
    /// </para>
    /// </summary>
    internal const int ExactCountThreshold = 10_000;

    private readonly string _deleteOutgoingEnvelopesSql;
    private readonly string _discardAndReassignOutgoingSql;
    private readonly string _findAtLargeEnvelopesSql;
    private readonly string _reassignIncomingSql;
    private readonly string _discardSupersededScheduledSql;
    private DatabaseServerId? _serverId;

    private readonly List<ISchemaObject> _externalTables = new();

    private ImHashMap<Type, IDatabaseSagaSchema> _sagaStorage = ImHashMap<Type, IDatabaseSagaSchema>.Empty;

    /// <summary>
    /// Returns the schema name properly quoted for use as a PostgreSQL identifier in SQL statements.
    /// </summary>
    protected override string QuotedSchemaName => SchemaName.QuoteIdentifier();

    /// <summary>
    /// GH-4216. PostgreSQL needs its schema name quoted; everything else about the mark-as-handled statement
    /// is shared, including the partition-aware form that inbox partitioning requires.
    /// </summary>
    protected override string MarkAsHandledTableName => $"{QuotedSchemaName}.{DatabaseConstants.IncomingTable}";


    public PostgresqlMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings, NpgsqlDataSource dataSource,
        ILogger<PostgresqlMessageStore> logger) : this(databaseSettings, settings, GetPrimaryNpgsqlNodeIfPossible(dataSource), logger, Array.Empty<SagaTableDefinition>())
    {
        // ReSharper disable once VirtualMemberCallInConstructor
        var descriptor = Describe();
        Id = new DatabaseId(descriptor.ServerName, descriptor.DatabaseName);
    }

    private static NpgsqlDataSource GetPrimaryNpgsqlNodeIfPossible(NpgsqlDataSource dataSource)
    {
        if (dataSource is NpgsqlMultiHostDataSource multiHost)
            return multiHost.WithTargetSession(TargetSessionAttributes.Primary);
        return dataSource;
    }

    // typeof(DatabaseSagaSchema<,>).CloseAndBuildAs<IDatabaseSagaSchema>(...) at L89
    // closes the saga schema generic over (sagaType, idType) at startup. Same
    // chunk D / I / J / K CloseAndBuildAs pattern: AOT-clean apps preserve
    // saga state types via TrimmerRootDescriptor. Cross-link to #2769.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "DatabaseSagaSchema<,> closed over runtime saga / id types at startup; AOT consumers preserve via TrimmerRootDescriptor. See AOT guide / #2769.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "DatabaseSagaSchema<,> closed over runtime saga / id types at startup; AOT consumers preserve via TrimmerRootDescriptor. See AOT guide / #2769.")]
    public PostgresqlMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings, NpgsqlDataSource dataSource,
        ILogger<PostgresqlMessageStore> logger, IEnumerable<SagaTableDefinition> sagaTypes) : base(databaseSettings, dataSource,
        settings, logger, new PostgresqlMigrator(), PostgresqlProvider.Instance)
    {
        // The incoming table's key shape follows MessageIdentity: received_at joins the primary key only
        // under IdAndDestination (see IncomingEnvelopeTable), and EnableInboxPartitioning then adds status
        // on top. Match the identity the same way ExistsAsync does, so both statements below agree with
        // whichever key the table was actually built with.
        string identityMatchClause(string alias, string other) => matchesIncomingById
            ? $"{alias}.id = {other}.id"
            : $"{alias}.id = {other}.id and {alias}.{DatabaseConstants.ReceivedAt} = {other}.{DatabaseConstants.ReceivedAt}";

        // With partitioning the same identity can also hold a Handled row, which the identity match on its
        // own would sweep into the incoming partition alongside the scheduled row.
        _reassignIncomingSql =
            $"update {QuotedSchemaName}.{DatabaseConstants.IncomingTable} target " +
            $"set owner_id = @owner, status = '{EnvelopeStatus.Incoming}' " +
            $"from unnest(@ids, @uris) as due(id, {DatabaseConstants.ReceivedAt}) " +
            $"where {identityMatchClause("target", "due")} " +
            $"and target.status = '{EnvelopeStatus.Scheduled}'";

        // With EnableInboxPartitioning the incoming table is PARTITION BY LIST (status) and status is part
        // of the primary key, so uniqueness is only enforced within a partition and one identity can legally
        // sit in the scheduled partition and in the incoming or handled partition at once. Promoting the
        // scheduled row moves it across partitions onto an identity another partition already holds, and
        // Postgres raises 23505 for the whole polling transaction. Either way the identity is already live in
        // another partition, so discard the scheduled copy and let the surviving row stand. Typically that copy
        // is a parked retry, since a locally scheduled send carries a fresh envelope id and cannot collide when
        // it is written, but the statement matches on the state rather than on how the row got there. Handled
        // supersedes for a second reason: promoting past it re-executes a message that already completed, and
        // marking that promotion handled would then collide on the handled partition's own key with nothing
        // left to try. The DELETE and the promotion are separate statements, so a row another connection commits
        // in between can still collide with the promotion. That costs one poll rather than every later one,
        // because the next poll sees the pair and discards it.
        _discardSupersededScheduledSql =
            $"delete from {QuotedSchemaName}.{DatabaseConstants.IncomingTable} scheduled " +
            $"using unnest(@ids, @uris) as due(id, {DatabaseConstants.ReceivedAt}) " +
            $"where {identityMatchClause("scheduled", "due")} " +
            $"and scheduled.status = '{EnvelopeStatus.Scheduled}' " +
            $"and exists (select 1 from {QuotedSchemaName}.{DatabaseConstants.IncomingTable} superseding " +
            $"where {identityMatchClause("superseding", "scheduled")} " +
            $"and superseding.status in ('{EnvelopeStatus.Incoming}', '{EnvelopeStatus.Handled}')) " +
            $"returning scheduled.id, scheduled.{DatabaseConstants.ReceivedAt}";

        _deleteOutgoingEnvelopesSql =
            $"delete from {QuotedSchemaName}.{DatabaseConstants.OutgoingTable} WHERE id = ANY(@ids);";

        _findAtLargeEnvelopesSql =
            $"select {DatabaseConstants.IncomingFields} from {QuotedSchemaName}.{DatabaseConstants.IncomingTable} where owner_id = {TransportConstants.AnyNode} and status = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.ReceivedAt} = :address limit :limit";

        _discardAndReassignOutgoingSql = _deleteOutgoingEnvelopesSql +
                                         $";update {QuotedSchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = @node where id = ANY(@rids)";

        // GH-4216: _markEnvelopeAsHandledById is no longer rebuilt here. It carries partition-aware logic now,
        // and rebuilding it in this constructor both dropped that logic and did so in the one provider that
        // supports inbox partitioning. Only the quoted table name differs, so that is what is overridden --
        // see MarkAsHandledTableName below.
        _incrementIncomingEnvelopeAttempts =
            $"update {QuotedSchemaName}.{DatabaseConstants.IncomingTable} set attempts = @attempts where id = @id and {DatabaseConstants.ReceivedAt} = @uri";

        NpgsqlDataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        AdvisoryLock = new AdvisoryLock(dataSource, logger, Identifier);
        
        foreach (var sagaTableDefinition in sagaTypes)
        {
            var storage = typeof(DatabaseSagaSchema<,>).CloseAndBuildAs<IDatabaseSagaSchema>(sagaTableDefinition, _settings, sagaTableDefinition.SagaType, sagaTableDefinition.IdMember.GetMemberType()!);
            _sagaStorage = _sagaStorage.AddOrUpdate(sagaTableDefinition.SagaType, storage);
        }
    }

    public NpgsqlDataSource NpgsqlDataSource { get; }
    
    /// <summary>
    ///     Fetch a list of the existing tables in the database
    /// </summary>
    /// <param name="database"></param>
    /// <returns></returns>
    public async Task<IReadOnlyList<DbObjectName>> SchemaTables(CancellationToken ct = default)
    {
        var schemaNames = AllSchemaNames();

        await using var conn = CreateConnection();

        await conn.OpenAsync(ct).ConfigureAwait(false);

        return await conn.ExistingTablesAsync(schemas: schemaNames, ct: ct).ConfigureAwait(false);
    }

    protected override INodeAgentPersistence? buildNodeStorage(DatabaseSettings databaseSettings,
        DbDataSource dataSource)
    {
        return new PostgresqlNodePersistence(databaseSettings, this, (NpgsqlDataSource)dataSource);
    }

    protected override bool isExceptionFromDuplicateEnvelope(Exception ex)
    {
        if (ex is PostgresException postgresException)
        {
            if (postgresException.SqlState == "23505") return true;
            return postgresException.Message.Contains("duplicate key value violates unique constraint");
        }

        return false;
    }

    protected override void writePagingAfter(DbCommandBuilder builder, int offset, int limit)
    {
        if (offset > 0)
        {
            builder.Append(" OFFSET ");
            builder.AppendParameter(offset);
        }
        
        if (limit > 0)
        {
            builder.Append(" LIMIT ");
            builder.AppendParameter(limit);
        }
    }

    public override string? BatchedDeleteExpiredHandledEnvelopesSql(int batchSize)
    {
        var table = $"{QuotedSchemaName}.{DatabaseConstants.IncomingTable}";

        // Bound the delete via ctid so each statement holds locks for a short time. The status
        // filter on both the inner select and the outer delete keeps partition pruning (and thus
        // ctid uniqueness) scoped to the _handled partition when inbox partitioning is enabled.
        return
            $"delete from {table} where {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}' and ctid in " +
            $"(select ctid from {table} where {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}' and {DatabaseConstants.KeepUntil} <= :now limit {batchSize});";
    }

    public override string? BatchedDeleteExpiredDeduplicationClaimsSql(int batchSize)
    {
        var table = $"{QuotedSchemaName}.{DatabaseConstants.DeduplicationTableName}";

        // Bounded via ctid, the same shape as BatchedDeleteExpiredHandledEnvelopesSql, so each
        // statement holds locks briefly instead of taking out a day's worth of claims at once.
        return
            $"delete from {table} where ctid in (select ctid from {table} where {DatabaseConstants.Expires} <= :now limit {batchSize});";
    }

    /// <summary>
    /// GH-3971: a "loose index scan" (skip scan), which PostgreSQL will not plan on its own. Descends
    /// the <c>owner_id</c> index once per DISTINCT value instead of reading every row, so the steady
    /// state — a handful of owners over millions of envelopes — costs a handful of index descents.
    ///
    /// <para>A plain <c>select distinct</c> would not do: with an index it can only manage an
    /// index-ONLY scan, and that still walks one entry per row and depends on visibility-map coverage
    /// the constantly-churning inbox does not have. It would simply move the full scan this change
    /// exists to remove from the UPDATE to the SELECT.</para>
    /// </summary>
    public override string DistinctOwnerIdsSql(DbObjectName table)
    {
        var owner = DatabaseConstants.OwnerId;

        return $@"
with recursive owners as (
    (select {owner} from {table} where {owner} <> 0 order by {owner} limit 1)
    union all
    select (select t.{owner} from {table} t where t.{owner} > o.{owner} and t.{owner} <> 0 order by t.{owner} limit 1)
    from owners o where o.{owner} is not null
)
select {owner} from owners where {owner} is not null";
    }

    public override string? BatchedReleaseOwnershipSql(DbObjectName table, string deadOwnerList, int batchSize)
    {
        var owner = DatabaseConstants.OwnerId;

        // Bound the update via ctid so each statement holds locks for a short time, the same shape as
        // BatchedDeleteExpiredHandledEnvelopesSql. Losing one node otherwise makes every envelope it
        // owned qualify in a single statement -- a reported deployment measured 587,460 buffers and 2,151
        // dirtied for one shard, with production bodies averaging 12 KB.
        return
            $"update {table} set {owner} = 0 where ctid in " +
            $"(select ctid from {table} where {owner} in ({deadOwnerList}) limit {batchSize});";
    }

    public override ISchemaObject AddExternalMessageTable(ExternalMessageTable definition)
    {
        var table = new Table(definition.TableName);
        table.AddColumn<Guid>(definition.IdColumnName).AsPrimaryKey();
        table.AddColumn(definition.JsonBodyColumnName, "jsonb").NotNull();
        if (definition.TimestampColumnName.IsNotEmpty())
        {
            table.AddColumn<DateTimeOffset>(definition.TimestampColumnName).DefaultValueByExpression("((now() at time zone 'utc'))");
        }

        if (definition.MessageTypeColumnName.IsNotEmpty())
        {
            table.AddColumn<string>(definition.MessageTypeColumnName);
        }
        
        return table;
    }
    
    public override async Task MigrateExternalMessageTable(ExternalMessageTable definition)
    {
        var table = (Table)AddExternalMessageTable(definition);
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await table.MigrateAsync(conn);
        await conn.CloseAsync();
    }

    protected override Task deleteMany(DbTransaction tx, Guid[] ids, DbObjectName tableName,
        string idColumnName)
    {
        return tx.CreateCommand($"delete from {tableName.QualifiedName} where {idColumnName} = ANY(@ids)")
            .As<NpgsqlCommand>().With("ids", ids).ExecuteNonQueryAsync();

    }

    protected override async Task<bool> TryAttainLockAsync(int lockId, NpgsqlConnection connection, CancellationToken token)
    {
        return await connection.TryGetGlobalLock(lockId, cancellation: token) == AttainLockResult.Success;
    }

    protected override Task ReleaseLockAsync(int lockId, NpgsqlConnection connection, CancellationToken token)
    {
        return connection.ReleaseGlobalLock(lockId, cancellation: token);
    }

    protected override DbCommand buildFetchSql(NpgsqlConnection conn, DbObjectName tableName, string[] columnNames, int maxRecords)
    {
        return conn.CreateCommand($"select {columnNames.Join(", ")} from {tableName.QualifiedName} LIMIT :limit")
            .With("limit", maxRecords);
    }

    public override async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        if (Durability.EnableInboxPartitioning)
        {
            await fetchCountsWithPartitionEstimates(counts);
        }
        else
        {
            await fetchCountsWithGroupBy(counts);
        }

        counts.Outgoing = await estimateTableCount(DatabaseConstants.OutgoingTable);
        counts.DeadLetter = await estimateTableCount(DatabaseConstants.DeadLetterTable);

        return counts;
    }

    private async Task<int> estimateTableCount(string tableName)
    {
        // Use pg_class reltuples for a fast estimation instead of expensive count(*).
        // Same approach as the partition estimation: handles never-vacuumed tables
        // (reltuples < 0) and empty tables (relpages = 0), then scales by current
        // relation size.
        //
        // GH-3885: the estimate alone is NOT good enough for small tables. Autoanalyze only
        // fires after ~(50 + 0.1 * reltuples) changed tuples, so a quiet dead letter or outgoing
        // table never re-analyzes and reports the same stale number on every single sample.
        // We therefore use the estimate as a cheap *gate*: when it comes back under
        // ExactCountThreshold, we pay for a real count(*) (cheap at that size); above the
        // threshold we keep the estimate, because that is where the volume -- and the cost of a
        // sequential scan -- actually lives.
        var sql = $@"
select (case when c.reltuples < 0 then 0
             when c.relpages = 0 then 0
             else (c.reltuples / c.relpages)
                  * (pg_catalog.pg_relation_size(c.oid)
                     / pg_catalog.current_setting('block_size')::int)
        end)::bigint as estimated_count,
       c.reltuples,
       pg_catalog.pg_relation_size(c.oid) as relation_size
from pg_catalog.pg_class c
join pg_catalog.pg_namespace n on n.oid = c.relnamespace and n.nspname = '{SchemaName}'
where c.relname = '{tableName}';";

        await using var reader = await CreateCommand(sql).ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var estimate = await reader.GetFieldValueAsync<long>(0);
            var reltuples = await reader.GetFieldValueAsync<float>(1);
            var relationSize = await reader.GetFieldValueAsync<long>(2);

            await reader.CloseAsync();

            // If the table has physical data but reltuples hasn't been updated
            // by VACUUM/ANALYZE, fall back to exact count
            if (reltuples <= 0 && relationSize > 0)
            {
                return await exactTableCount(tableName);
            }

            // GH-3885: small tables never re-analyze, so their estimate is permanently stale.
            // count(*) is cheap here -- take the accurate answer.
            if (estimate < ExactCountThreshold)
            {
                return await exactTableCount(tableName);
            }

            return (int)estimate;
        }

        await reader.CloseAsync();
        return 0;
    }

    private async Task<int> exactTableCount(string tableName)
    {
        var exactCount = await CreateCommand($"select count(*) from {QuotedSchemaName}.{tableName}")
            .ExecuteScalarAsync();
        return Convert.ToInt32(exactCount);
    }

    private async Task fetchCountsWithGroupBy(PersistedCounts counts)
    {
        await using var reader = await CreateCommand(
                $"select status, count(*) from {QuotedSchemaName}.{DatabaseConstants.IncomingTable} group by status")
            .ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(0));
            var count = await reader.GetFieldValueAsync<int>(1);

            if (status == EnvelopeStatus.Incoming)
            {
                counts.Incoming = count;
            }
            else if (status == EnvelopeStatus.Handled)
            {
                counts.Handled = count;
            }
            else if (status == EnvelopeStatus.Scheduled)
            {
                counts.Scheduled = count;
            }
        }

        await reader.CloseAsync();
    }

    private async Task fetchCountsWithPartitionEstimates(PersistedCounts counts)
    {
        // Use pg_class reltuples to estimate row counts per partition.
        // This is the "Safe and Explicit" approach from
        // https://stackoverflow.com/questions/7943233: handles never-vacuumed
        // tables (reltuples < 0) and empty tables (relpages = 0), then
        // scales the estimate by the current relation size.
        // If any partition has data (pg_relation_size > 0) but stale stats
        // (reltuples <= 0), we fall back to exact GROUP BY count.
        var sql = $@"
select p.partition_name, c.reltuples,
       pg_catalog.pg_relation_size(c.oid) as relation_size,
       (case when c.reltuples < 0 then 0
             when c.relpages = 0 then 0
             else (c.reltuples / c.relpages)
                  * (pg_catalog.pg_relation_size(c.oid)
                     / pg_catalog.current_setting('block_size')::int)
        end)::bigint as estimated_count
from pg_catalog.pg_class c
join (values
    ('{DatabaseConstants.IncomingTable}_incoming',  'Incoming'),
    ('{DatabaseConstants.IncomingTable}_scheduled', 'Scheduled'),
    ('{DatabaseConstants.IncomingTable}_handled',   'Handled')
) as p(relname, partition_name) on c.relname = p.relname
join pg_catalog.pg_namespace n on n.oid = c.relnamespace and n.nspname = '{SchemaName}';";

        var needsFallback = false;

        await using (var reader = await CreateCommand(sql).ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var partitionName = await reader.GetFieldValueAsync<string>(0);
                var reltuples = await reader.GetFieldValueAsync<float>(1);
                var relationSize = await reader.GetFieldValueAsync<long>(2);
                var estimate = await reader.GetFieldValueAsync<long>(3);

                // If the partition has physical data but reltuples hasn't been
                // updated by VACUUM/ANALYZE, the estimate will be wrong.
                if (reltuples <= 0 && relationSize > 0)
                {
                    needsFallback = true;
                    break;
                }

                switch (partitionName)
                {
                    case "Incoming":
                        counts.Incoming = (int)estimate;
                        break;
                    case "Scheduled":
                        counts.Scheduled = (int)estimate;
                        break;
                    case "Handled":
                        counts.Handled = (int)estimate;
                        break;
                }
            }

            await reader.CloseAsync();
        }

        if (needsFallback)
        {
            await fetchCountsWithGroupBy(counts);
        }
    }

    protected override async Task afterTruncateEnvelopeDataAsync(DbConnection conn)
    {
        // After deleting data, PostgreSQL's pg_class.reltuples statistics become stale.
        // FetchCountsAsync() uses these stats for fast estimation, so we must run ANALYZE
        // to update them after bulk deletes.
        await conn.CreateCommand($"ANALYZE {QuotedSchemaName}.{DatabaseConstants.DeadLetterTable}")
            .ExecuteNonQueryAsync(_cancellation);
        await conn.CreateCommand($"ANALYZE {QuotedSchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteNonQueryAsync(_cancellation);

        if (Durability.EnableInboxPartitioning)
        {
            await conn.CreateCommand($"ANALYZE {QuotedSchemaName}.{DatabaseConstants.IncomingTable}")
                .ExecuteNonQueryAsync(_cancellation);
        }
    }

    public override async Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        await using var cmd = CreateCommand(_discardAndReassignOutgoingSql)
            .WithEnvelopeIds("ids", discards)
            .With("node", nodeId)
            .WithEnvelopeIds("rids", reassigned);

        await cmd.ExecuteNonQueryAsync(_cancellation);
    }

    public override async Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        if (HasDisposed) return;

        await CreateCommand(_deleteOutgoingEnvelopesSql)
            .WithEnvelopeIds("ids", envelopes)
            .ExecuteNonQueryAsync(_cancellation);
    }

    protected override string determineOutgoingEnvelopeSql(DurabilitySettings settings)
    {
        return
            $"select {DatabaseConstants.OutgoingFields} from {QuotedSchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = {TransportConstants.AnyNode} and destination = @destination LIMIT {settings.RecoveryBatchSize}";
    }

    public override async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress,
        int limit)
    {
        return await CreateCommand(_findAtLargeEnvelopesSql)
            .With("address", listenerAddress.ToString())
            .With("limit", limit)
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r));
    }

    public override DbCommandBuilder ToCommandBuilder()
    {
        return new DbCommandBuilder(new NpgsqlCommand());
    }

    public override async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (HasDisposed) return false;

        if (Durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            await using var conn = await NpgsqlDataSource.OpenConnectionAsync(cancellation);
            var count = await conn
                .CreateCommand($"select count(id) from {QuotedSchemaName}.{DatabaseConstants.IncomingTable} where id = :id")
                .With("id", envelope.Id)
                .ExecuteScalarAsync(cancellation);

            return ((long)count!) > 0;
        }
        else
        {
            await using var conn = await NpgsqlDataSource.OpenConnectionAsync(cancellation);
            var count = await conn
                .CreateCommand($"select count(id) from {QuotedSchemaName}.{DatabaseConstants.IncomingTable} where id = :id and {DatabaseConstants.ReceivedAt} = :destination")
                .With("id", envelope.Id)
                .With("destination", envelope.Destination!.ToString())
                .ExecuteScalarAsync(cancellation);

            return ((long)count!) > 0;
        }
    }

    public override void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow)
    {
        builder.Append(
            $"select {DatabaseConstants.IncomingFields} from {QuotedSchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= ");

        builder.AppendParameter(utcNow);
        builder.Append($" order by execution_time LIMIT {Durability.RecoveryBatchSize};");
    }

    public override async Task PollForScheduledMessagesAsync(IWolverineRuntime runtime, ILogger logger,
        DurabilitySettings durabilitySettings, CancellationToken cancellationToken)
    {
        IReadOnlyList<Envelope> envelopes;

        if (HasDisposed) return;

        await using var conn = await NpgsqlDataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            // GH-3664: this is a transaction-scoped advisory lock, and Marten's async-daemon gap-liveness
            // gate (marten#4953/#5057) treats ANY session with an open transaction older than an
            // event-sequence gap as a possible reserver of that gap. This transaction must therefore stay
            // short — do the poll's work and commit/rollback promptly, never await anything that isn't a
            // command on this connection while it is open, and never add keepalive queries inside it
            // (bumping state_change re-promotes the session to candidate reserver and freezes projections
            // behind dead gaps). Long-held exclusivity belongs on a session-scoped lock on a
            // transaction-free dedicated connection instead — see AdvisoryLock in PostgresqlNodePersistence.
            var tx = await conn.BeginTransactionAsync(cancellationToken);
            if (await tx.TryGetGlobalTxLock(Settings.ScheduledJobLockId, cancellationToken) == AttainLockResult.Success)
            {
                var builder = new DbCommandBuilder(conn);
                WriteLoadScheduledEnvelopeSql(builder, DateTimeOffset.UtcNow);
                await using var cmd = builder.Compile();
                cmd.Connection = conn;
                cmd.Transaction = tx;

                envelopes = await cmd.FetchListAsync(reader =>
                    DatabasePersistence.ReadIncomingAsync(reader, cancellationToken), cancellation: cancellationToken);

                if (!envelopes.Any())
                {
                    await tx.RollbackAsync(cancellationToken);
                    return;
                }

                var (promotable, superseded) =
                    await discardSupersededScheduledEnvelopesAsync(conn, tx, envelopes, cancellationToken);

                if (!promotable.Any())
                {
                    await tx.CommitAsync(cancellationToken);
                    announceSupersededScheduledEnvelopes(logger, superseded);
                    return;
                }

                envelopes = promotable;

                var (promotableIds, promotableUris) = toIdAndUriArrays(envelopes);

                await using var reassign = conn.CreateCommand(_reassignIncomingSql);
                reassign.Transaction = tx;
                await reassign
                    .With("owner", durabilitySettings.AssignedNodeNumber)
                    .With("ids", promotableIds)
                    .With("uris", promotableUris)
                    .ExecuteNonQueryAsync(_cancellation);


                await tx.CommitAsync(cancellationToken);

                announceSupersededScheduledEnvelopes(logger, superseded);

                // Stamp the envelope's owning store on each row so the rest of the
                // pipeline (DelegatingMessageInbox, FlushOutgoingMessagesOnCommit,
                // DurableReceiver._markAsHandled) routes its writes back to THIS
                // store. Without this, an ancillary store's scheduled message wakes
                // up with envelope.Store == null and the mark-as-handled SQL goes
                // to the main store, leaving the row stuck Incoming.
                // See https://github.com/JasperFx/wolverine/issues/2576.
                foreach (var envelope in envelopes)
                {
                    envelope.Store = this;
                }

                // Judging that there's very little chance of errors here
                await runtime.EnqueueDirectlyAsync(envelopes);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private bool matchesIncomingById => Durability.MessageIdentity == MessageIdentity.IdOnly;

    private static (Guid[] Ids, string[] Uris) toIdAndUriArrays(IReadOnlyList<Envelope> envelopes)
    {
        var ids = new Guid[envelopes.Count];
        var uris = new string[envelopes.Count];

        for (var i = 0; i < envelopes.Count; i++)
        {
            ids[i] = envelopes[i].Id;
            uris[i] = envelopes[i].Destination!.ToString();
        }

        return (ids, uris);
    }

    private async Task<(IReadOnlyList<Envelope> Promotable, IReadOnlyList<Envelope> Superseded)>
        discardSupersededScheduledEnvelopesAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
            IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken)
    {
        if (!Durability.EnableInboxPartitioning) return (envelopes, []);

        var (ids, uris) = toIdAndUriArrays(envelopes);

        await using var command = conn.CreateCommand(_discardSupersededScheduledSql);
        command.Transaction = tx;
        command.With("ids", ids).With("uris", uris);

        var discarded = new HashSet<(Guid, string)>();

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                discarded.Add(discardKey(await reader.GetFieldValueAsync<Guid>(0, cancellationToken),
                    await reader.GetFieldValueAsync<string>(1, cancellationToken)));
            }
        }

        if (discarded.Count == 0) return (envelopes, []);

        var promotable = new List<Envelope>(envelopes.Count);
        var superseded = new List<Envelope>();

        foreach (var envelope in envelopes)
        {
            if (discarded.Contains(discardKey(envelope.Id, envelope.Destination!.ToString())))
            {
                superseded.Add(envelope);
            }
            else
            {
                promotable.Add(envelope);
            }
        }

        return (promotable, superseded);
    }

    // Keyed the way the DELETE keys its rows: under IdOnly the statement matches on id alone, so folding
    // received_at into the set would let the in-memory partition disagree with the rows actually removed.
    private (Guid, string) discardKey(Guid id, string receivedAt)
        => matchesIncomingById ? (id, string.Empty) : (id, receivedAt);

    // Logged only after the enclosing transaction commits. The DELETE is not durable until then, and a
    // failure in the promotion that follows it rolls the discard back, so reporting earlier would tell an
    // operator a row was dropped when it is still there.
    private static void announceSupersededScheduledEnvelopes(ILogger logger, IReadOnlyList<Envelope> superseded)
    {
        foreach (var envelope in superseded)
        {
            logger.LogWarning(
                "Discarded scheduled envelope {EnvelopeId} of type {MessageType} for destination {Destination} after {Attempts} attempts because the same identity is already present in the incoming or handled partition.",
                envelope.Id, envelope.MessageType, envelope.Destination, envelope.Attempts);
        }
    }

    public override async Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string messageTypeName, byte[] json,
        CancellationToken token)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        if (table.MessageTypeColumnName.IsEmpty())
        {
            await conn.CreateCommand(
                    $"insert into {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}) values (@id, @json)")
                .With("id", Guid.NewGuid())
                .With("json", json, NpgsqlDbType.Jsonb)
                .ExecuteNonQueryAsync(token);
        }
        else
        {
            await conn.CreateCommand(
                    $"insert into {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}, {table.MessageTypeColumnName}) values (@id, @json, @message)")
                .With("id", Guid.NewGuid())
                .With("json", json, NpgsqlDbType.Jsonb)
                .With("message", messageTypeName)
                .ExecuteNonQueryAsync(token);
        }
        
        await conn.CloseAsync();
    }

    /// <summary>
    /// The Postgres cluster this database lives on (#3397). The port is carried explicitly rather
    /// than left implicit in the host, because <see cref="DatabaseDescriptor.ServerName"/> is
    /// host-only and two clusters co-hosted on one box would otherwise collide onto a single budget.
    /// </summary>
    public DatabaseServerId ServerId
    {
        get
        {
            if (_serverId.HasValue)
            {
                return _serverId.Value;
            }

            // Sourced from Describe() so the server id and the diagnostic descriptor can't disagree
            // about the host/port. Cached, so the descriptor is only built once.
            _serverId = DatabaseServerId.For(Describe());

            return _serverId.Value;
        }
    }

    public async ValueTask<int> CountServerConnectionsAsync(CancellationToken token)
    {
        // Server-wide, and deliberately not filtered to this database or this application:
        // connections are a resource of the cluster, and a budget that ignored the other tenants
        // (or the other applications) sharing it would be measuring the wrong thing. Note that
        // behind a transaction-pooling pgBouncer this counts pooler-to-server backends rather than
        // client sessions — see the connection-budget docs.
        var raw = await CreateCommand("select coalesce(sum(numbackends), 0)::int from pg_catalog.pg_stat_database")
            .ExecuteScalarAsync(token).ConfigureAwait(false);

        return raw is int count ? count : 0;
    }

    public async ValueTask<int?> ProbeMaxConnectionsAsync(CancellationToken token)
    {
        var raw = await CreateCommand("select current_setting('max_connections')::int")
            .ExecuteScalarAsync(token).ConfigureAwait(false);

        return raw is int max && max > 0 ? max : null;
    }

    public override DatabaseDescriptor Describe()
    {
        var builder = new NpgsqlConnectionStringBuilder(DataSource?.ConnectionString ?? Settings.ConnectionString);
        var descriptor = new DatabaseDescriptor()
        {
            Engine = "PostgreSQL",
            ServerName = builder.Host ?? string.Empty,
            // PostgreSQL carries the port separately from the host, and it matters for connection
            // budgeting: two clusters co-hosted on one box would otherwise collide onto one budget.
            Port = builder.Port,
            DatabaseName = builder.Database ?? string.Empty,
            Subject = GetType().FullNameInCode(),
            SubjectUri = SubjectUri,
            Identifier = Identifier
        };
        
        descriptor.TenantIds.AddRange(TenantIds);

        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Host!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Port));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Database!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Username!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ApplicationName!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Enlist));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SearchPath!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ClientEncoding!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Encoding));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Timezone!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SslMode));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SslNegotiation));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.CheckCertificateRevocation));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.KerberosServiceName));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.IncludeRealm));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.PersistSecurityInfo));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.LogParameters));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.IncludeErrorDetail));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ChannelBinding));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Pooling));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MinPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MaxPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ConnectionIdleLifetime));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ConnectionPruningInterval));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ConnectionLifetime));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Timeout));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.CommandTimeout));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.CancellationTimeout));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TargetSessionAttributes!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.LoadBalanceHosts));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.HostRecheckSeconds));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.KeepAlive));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TcpKeepAlive));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TcpKeepAliveTime));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TcpKeepAliveInterval));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ReadBufferSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.WriteBufferSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SocketReceiveBufferSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SocketSendBufferSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MaxAutoPrepare));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.AutoPrepareMinUsages));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.NoResetOnClose));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Options!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ArrayNullabilityMode));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Multiplexing));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.WriteCoalescingBufferThresholdBytes));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.LoadTableComposites));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ServerCompatibilityMode));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TrustServerCertificate));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.InternalCommandTimeout));

        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("password"));
        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("certificate"));

        return descriptor;
    }

    public override IEnumerable<ISchemaObject> AllObjects()
    {
        yield return new OutgoingEnvelopeTable(Durability, SchemaName);
        yield return new IncomingEnvelopeTable(Durability, SchemaName);
        yield return new DeadLettersTable(Durability, SchemaName);

        // GH-4180. Every store role, not just Main: a handler chain with an AncillaryStoreType claims
        // its logical id in that store so the claim and the work land in one transaction.
        if (Durability.EnableMessageDeduplication)
        {
            yield return new DeduplicationTable(SchemaName);
        }

        foreach (var table in _externalTables)
        {
            yield return table;
        }

        if (Role == MessageStoreRole.Main)
        {
            var nodeTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeTableName));
            nodeTable.AddColumn<Guid>("id").AsPrimaryKey();
            nodeTable.AddColumn("node_number", "SERIAL").NotNull();
            nodeTable.AddColumn<string>("description").NotNull();
            nodeTable.AddColumn<string>("uri").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("now()").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("health_check").NotNull().DefaultValueByExpression("now()");
            nodeTable.AddColumn<string>("version");
            nodeTable.AddColumn("capabilities", "text[]").AllowNulls();

            yield return nodeTable;

            var assignmentTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeAssignmentsTableName));
            assignmentTable.AddColumn<string>("id").AsPrimaryKey();
            assignmentTable.AddColumn<Guid>("node_id")
                .ForeignKeyTo(nodeTable.Identifier, "id", onDelete: CascadeAction.Cascade);
            assignmentTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("now()").NotNull();

            yield return assignmentTable;

            if (_settings.CommandQueuesEnabled)
            {
                var queueTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.ControlQueueTableName));
                queueTable.AddColumn<Guid>("id").AsPrimaryKey();
                queueTable.AddColumn<string>("message_type").NotNull();
                queueTable.AddColumn<Guid>("node_id").NotNull();
                queueTable.AddColumn(DatabaseConstants.Body, "bytea").NotNull();
                queueTable.AddColumn<DateTimeOffset>("posted").NotNull().DefaultValueByExpression("NOW()");
                queueTable.AddColumn<DateTimeOffset>("expires");

                yield return queueTable;
            }

            if (_settings.AddTenantLookupTable)
            {
                var tenantTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.TenantsTableName));
                tenantTable.AddColumn<string>(StorageConstants.TenantIdColumn).AsPrimaryKey();
                tenantTable.AddColumn<string>(StorageConstants.ConnectionStringColumn).NotNull();
                tenantTable.AddColumn<bool>(DatabaseConstants.DisabledColumn).DefaultValueByExpression("false").NotNull();
                yield return tenantTable;
            }

            var eventTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeRecordTableName));
            eventTable.AddColumn("id", "SERIAL").AsPrimaryKey();
            eventTable.AddColumn<int>("node_number").NotNull();
            eventTable.AddColumn<string>("event_name").NotNull();
            eventTable.AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("now()").NotNull();
            eventTable.AddColumn<string>("description").AllowNulls();
            yield return eventTable;
            
            var restrictionTable =
                new Table(new DbObjectName(SchemaName, DatabaseConstants.AgentRestrictionsTableName));
            restrictionTable.AddColumn<Guid>("id").AsPrimaryKey();
            restrictionTable.AddColumn<string>("uri").NotNull();
            restrictionTable.AddColumn<string>("type").NotNull();
            restrictionTable.AddColumn<int>("node").NotNull().DefaultValue(0);
            yield return restrictionTable;

            // Dynamic listener registry (GH-2685). Provisioned only when the opt-in
            // flag is set so existing apps see no migration churn.
            if (Durability.EnableDynamicListeners)
            {
                var listenerTable =
                    new Table(new DbObjectName(SchemaName, DatabaseConstants.ListenersTableName));
                listenerTable.AddColumn<string>("uri").AsPrimaryKey();
                yield return listenerTable;
            }
        }
        
        foreach (var table in _otherTables)
        {
            yield return table;
        }
            
        foreach (var entry in _sagaStorage.Enumerate())
        {
            yield return entry.Value.Table;
        }
    }

    private readonly List<Table> _otherTables = new();

    public void AddTable(Table table)
    {
        _otherTables.Add(table);
    }

    public override DatabaseSagaSchema<T, TId> SagaSchemaFor<T, TId>()
    {
        if (_sagaStorage.TryFind(typeof(T), out var raw))
        {
            if (raw is DatabaseSagaSchema<T, TId> sagaStorage)
            {
                return sagaStorage;
            }
        }
        
        var definition = new SagaTableDefinition(typeof(T), null);
        var storage = new DatabaseSagaSchema<T, TId>(definition, _settings);
        _sagaStorage = _sagaStorage.AddOrUpdate(typeof(T), storage);
        
        return storage;
    }

    protected override void writeMessageIdArrayQueryList(DbCommandBuilder builder, Guid[] messageIds)
    {
        builder.Append($" and {DatabaseConstants.Id} = ANY(");
        builder.AppendParameter(messageIds);
        builder.Append(')');
    }

    public override async Task DeleteAllHandledAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(CancellationToken.None);

        var deleted = 1;

        var sql = $@"
        WITH todo AS (
            SELECT id
            FROM {QuotedSchemaName}.{DatabaseConstants.IncomingTable}
            WHERE status = '{EnvelopeStatus.Handled}'
            ORDER BY id
            LIMIT 10000
            FOR UPDATE SKIP LOCKED
        )
        DELETE FROM {QuotedSchemaName}.{DatabaseConstants.IncomingTable} w
        USING todo
        WHERE w.id = todo.id;
";
        
        try
        {
            while (deleted > 0)
            {
                await using var cmd = conn.CreateCommand(sql);
                deleted = await cmd.ExecuteNonQueryAsync();
                await Task.Delay(10.Milliseconds());
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}