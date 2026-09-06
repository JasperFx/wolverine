using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql;

/// <summary>
/// GH-4320. Fixed-arity batched inserts: <c>unnest</c> of one array per column, so a 100-envelope batch
/// is 7-9 parameters rather than ~700-900. The same shape the scheduled-promotion path has used since
/// GH-4209.
///
/// <para>
/// <b>MEASURED at ~1.35x on the batched insert</b> (in-build A/B against the legacy shape, varying batch
/// sizes: 1.318 -> 0.988ms and 1.057 -> 0.771ms). The win is the smaller command and the parameter
/// count -- binding 900 parameters client-side and parsing them server-side is simply more work than
/// binding 9.
/// </para>
///
/// <para>
/// <b>It is NOT the plan-cache win the issue predicted, and that prediction did not survive
/// measurement.</b> GH-4320 argued that unique-per-batch-size command text defeats Npgsql's
/// <c>MaxAutoPrepare</c>. Two things turned out to be wrong with that. Wolverine never sets
/// <c>Max Auto Prepare</c> and Npgsql defaults it to 0, so no Wolverine command was being auto-prepared
/// either way; and when it IS switched on, the advantage of fixed arity largely disappears (0.97x and
/// 1.19x over the legacy shape at varying batch sizes -- noise). Prepared-statement reuse was never
/// where the cost was.
/// </para>
///
/// <para>
/// The structural benefit that does hold independently of any measurement: parameter count no longer
/// scales with batch size, so a batch cannot walk towards a provider's parameter ceiling.
/// </para>
///
/// <para>
/// Every parameter is typed EXPLICITLY. That is not defensive style: #4356 broke SQL Server by letting
/// a helper infer a type, and array parameters are worse for inference than scalars -- an all-null
/// <c>timestamptz[]</c> has nothing to infer from at all.
/// </para>
/// </summary>
internal partial class PostgresqlMessageStore
{
    // GH-4375. PostgreSQL's wire protocol caps parameters at 65,535 (an int16 count).
    public override int MaximumParameterCount => 65_535;

    // GH-4320 moved both batched builders to unnest, so parameter count no longer scales with batch size
    protected override bool BuildsFixedArityBatches => true;

    private string? _batchedIncomingSql;
    private string? _batchedOutgoingSql;

    private string batchedIncomingSql()
    {
        return _batchedIncomingSql ??=
            $"insert into {QuotedTableNameFor(DatabaseConstants.IncomingTable)}({DatabaseConstants.IncomingFields}) " +
            "select * from unnest(@bodies, @ids, @statuses, @owners, @executions, @attempts, @types, @destinations, @keeps)";
    }

    private string batchedOutgoingSql()
    {
        return _batchedOutgoingSql ??=
            $"insert into {QuotedTableNameFor(DatabaseConstants.OutgoingTable)}({DatabaseConstants.OutgoingFields}) " +
            "select * from unnest(@bodies, @ids, @owners, @destinations, @deliverBys, @attempts, @types)";
    }

    protected override DbCommand BuildBatchedIncomingCommand(IReadOnlyList<Envelope> envelopes)
    {
        var count = envelopes.Count;

        var bodies = new byte[count][];
        var ids = new Guid[count];
        var statuses = new string[count];
        var owners = new int[count];
        var executions = new DateTimeOffset?[count];
        var attempts = new int[count];
        var types = new string[count];
        var destinations = new string?[count];
        var keeps = new DateTimeOffset?[count];

        for (var i = 0; i < count; i++)
        {
            var envelope = envelopes[i];

            // Same rule the per-envelope builder applies: a handled envelope stores no body
            bodies[i] = envelope.Status == EnvelopeStatus.Handled
                ? []
                : Runtime.Serialization.EnvelopeSerializer.Serialize(envelope);

            ids[i] = envelope.Id;
            statuses[i] = envelope.Status.ToString();
            owners[i] = envelope.OwnerId;
            executions[i] = envelope.ScheduledTime;
            attempts[i] = envelope.Attempts;
            types[i] = envelope.MessageType!;
            destinations[i] = envelope.Destination?.ToString();
            keeps[i] = envelope.KeepUntil;
        }

        var command = new NpgsqlCommand(batchedIncomingSql());
        add(command, "bodies", NpgsqlDbType.Array | NpgsqlDbType.Bytea, bodies);
        add(command, "ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
        add(command, "statuses", NpgsqlDbType.Array | NpgsqlDbType.Varchar, statuses);
        add(command, "owners", NpgsqlDbType.Array | NpgsqlDbType.Integer, owners);
        add(command, "executions", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, executions);
        add(command, "attempts", NpgsqlDbType.Array | NpgsqlDbType.Integer, attempts);
        add(command, "types", NpgsqlDbType.Array | NpgsqlDbType.Varchar, types);
        add(command, "destinations", NpgsqlDbType.Array | NpgsqlDbType.Varchar, destinations);
        add(command, "keeps", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, keeps);

        return command;
    }

    protected override DbCommand BuildBatchedOutgoingCommand(Envelope[] envelopes, int ownerId)
    {
        var count = envelopes.Length;

        var bodies = new byte[count][];
        var ids = new Guid[count];
        var owners = new int[count];
        var destinations = new string[count];
        var deliverBys = new DateTimeOffset?[count];
        var attempts = new int[count];
        var types = new string[count];

        for (var i = 0; i < count; i++)
        {
            var envelope = envelopes[i];

            bodies[i] = Runtime.Serialization.EnvelopeSerializer.Serialize(envelope);
            ids[i] = envelope.Id;
            owners[i] = ownerId;
            destinations[i] = envelope.Destination!.ToString();
            deliverBys[i] = envelope.DeliverBy;
            attempts[i] = envelope.Attempts;
            types[i] = envelope.MessageType!;
        }

        var command = new NpgsqlCommand(batchedOutgoingSql());
        add(command, "bodies", NpgsqlDbType.Array | NpgsqlDbType.Bytea, bodies);
        add(command, "ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
        add(command, "owners", NpgsqlDbType.Array | NpgsqlDbType.Integer, owners);
        add(command, "destinations", NpgsqlDbType.Array | NpgsqlDbType.Varchar, destinations);
        add(command, "deliverBys", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, deliverBys);
        add(command, "attempts", NpgsqlDbType.Array | NpgsqlDbType.Integer, attempts);
        add(command, "types", NpgsqlDbType.Array | NpgsqlDbType.Varchar, types);

        return command;
    }

    /// <summary>
    /// The plan-stability tests assert on the command text and parameter count, which is the property a
    /// plan cache actually keys on and the one thing the correctness suites cannot see. Exposed rather
    /// than reflected at, so the assertion breaks loudly if the shape is refactored away.
    /// </summary>
    internal DbCommand TestingOnlyBuildBatchedIncoming(IReadOnlyList<Envelope> envelopes)
        => BuildBatchedIncomingCommand(envelopes);

    internal DbCommand TestingOnlyBuildBatchedOutgoing(Envelope[] envelopes, int ownerId)
        => BuildBatchedOutgoingCommand(envelopes, ownerId);

    private static void add(NpgsqlCommand command, string name, NpgsqlDbType type, object value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
    }
}
