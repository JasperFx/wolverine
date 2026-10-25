using System.Data.Common;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public static class DatabasePersistence
{
    /// <summary>
    ///     GH-5073. The order an inbox recovery claim updates its candidate rows in.
    /// </summary>
    /// <remarks>
    ///     A claim updates its page one row at a time inside one transaction, so every row it wins stays
    ///     locked until the page commits. Two claims over the same rows in different orders then wait on each
    ///     other: the review of GH-5073 deadlocked PostgreSQL, SQL Server and MySQL that way on most attempts.
    ///     The page query has no ORDER BY, so nothing promises two recovery commands load a page in the same
    ///     order. Claiming in one total order makes every claimer take its locks in the same sequence, and the
    ///     loser simply waits for the winner's commit and then finds the rows already owned.
    /// </remarks>
    internal static IReadOnlyList<Envelope> InClaimOrder(IReadOnlyList<Envelope> candidates)
    {
        return candidates
            .OrderBy(x => x.Id)
            .ThenBy(x => x.Destination?.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     Mirror onto the in-memory envelopes what a scheduled-message promotion just did to their rows,
    ///     before they are handed to <see cref="IWolverineRuntime.EnqueueDirectlyAsync" />.
    /// </summary>
    /// <remarks>
    ///     <para>GH-2576 is the <c>Store</c> half: without it an ancillary store's scheduled message wakes up
    ///     with a null store and its mark-as-handled SQL goes to the main store, leaving the row Incoming.</para>
    ///
    ///     <para>GH-4673 is the status half, and it fails the same way for a different reason. The promotion
    ///     UPDATE sets the row to <c>Incoming</c> owned by this node, but the envelope was READ while it was
    ///     still <c>Scheduled</c>, so its in-memory <c>Status</c> still said so. That is not cosmetic:
    ///     <c>Envelope.IsScheduledForLater</c> answers true on status ALONE — "if it's been scheduled and
    ///     persisted, it has to be scheduled", regardless of the clock — so when the envelope reached a
    ///     bridged or non-local listener, <c>MarkReceived</c> re-asserted <c>Scheduled</c> and
    ///     <c>DurableReceiver.receiveOneAsync</c> then declined to execute it, silently, because it only
    ///     enqueues an envelope whose status is <c>Incoming</c>.</para>
    ///
    ///     <para>The row was left <c>Incoming</c> under a LIVE owner, which no recovery path can reclaim:
    ///     inbox recovery only takes <c>owner_id = 0</c>, and the orphan sweep only releases nodes proven
    ///     dead. So the message was wedged for the life of the process with nothing logged.</para>
    ///
    ///     <para>Stamping both here keeps the five database stores from drifting; each previously carried
    ///     its own copy of the <c>Store</c> loop.</para>
    /// </remarks>
    public static void MarkPromotedFromScheduled(IReadOnlyList<Envelope> envelopes, IMessageStore store,
        int ownerId)
    {
        foreach (var envelope in envelopes)
        {
            envelope.Store = store;
            envelope.Status = EnvelopeStatus.Incoming;
            envelope.OwnerId = ownerId;
        }
    }

    public static DbCommand BuildOutgoingStorageCommand(Envelope envelope, int ownerId,
        IMessageDatabase database)
    {
        var builder = database.ToCommandBuilder();

        var owner = builder.AddNamedParameter("owner", ownerId);
        ConfigureOutgoingCommand(database, builder, envelope, owner);
        return builder.Compile();
    }

    public static DbCommand BuildOutgoingStorageCommand(Envelope[] envelopes, int ownerId,
        IMessageDatabase database)
    {
        var builder = database.ToCommandBuilder();

        var owner = builder.AddNamedParameter("owner", ownerId);

        foreach (var envelope in envelopes) ConfigureOutgoingCommand(database, builder, envelope, owner);

        return builder.Compile();
    }

    /// <summary>
    /// GH-4320. Register a parameter and write its placeholder, WITHOUT touching parameter typing.
    ///
    /// <para>
    /// Deliberately <c>AddParameter(value)</c> with no dbType, exactly as the original code did:
    /// that path never calls <c>SetParameterType</c>, so ADO.NET infers the type from the value.
    /// The first attempt at this optimization (#4356, reverted in #4358) used Weasel's
    /// <c>AppendParameter</c> helpers instead, which resolve to typed overloads that DO set an
    /// explicit provider type -- and through the generic DbCommandBuilder a Guid then reached SQL
    /// Server as <c>sql_variant</c> against a <c>uniqueidentifier</c> column. The saving here is
    /// the per-envelope List + LINQ + string.Join, nothing more.
    /// </para>
    /// </summary>
    private static void appendValue(DbCommandBuilder builder, object? value)
    {
        var parameter = builder.AddParameter(value);
        builder.Append('@');
        builder.Append(parameter.ParameterName);
    }

    private static void ConfigureOutgoingCommand(IMessageDatabase settings, DbCommandBuilder builder, Envelope envelope,
        DbParameter owner)
    {
        builder.Append(
            $"insert into {settings.TableNameFor(DatabaseConstants.OutgoingTable)} ({DatabaseConstants.OutgoingFields}) values (");
        appendValue(builder, EnvelopeSerializer.Serialize(envelope));
        builder.Append(", ");
        appendValue(builder, envelope.Id);
        builder.Append($", @{owner.ParameterName}, ");
        appendValue(builder, envelope.Destination!.ToString());
        builder.Append(", ");
        appendValue(builder, envelope.DeliverBy);
        builder.Append(", ");
        appendValue(builder, envelope.Attempts);
        builder.Append(", ");
        appendValue(builder, envelope.MessageType);
        builder.Append(");");
    }

    public static DbCommand BuildIncomingStorageCommand(IEnumerable<Envelope> envelopes,
        IMessageDatabase database)
    {
        var builder = database.ToCommandBuilder();

        foreach (var envelope in envelopes) BuildIncomingStorageCommand(database, builder, envelope);

        return builder.Compile();
    }

    public static void BuildIncomingStorageCommand(IMessageDatabase settings, DbCommandBuilder builder,
        Envelope envelope)
    {
        // Don't store any data if the envelope is already marked as handled
        var data = envelope.Status == EnvelopeStatus.Handled ? [] : EnvelopeSerializer.Serialize(envelope);
        
        builder.Append(
            $@"insert into {settings.TableNameFor(DatabaseConstants.IncomingTable)}({DatabaseConstants.IncomingFields}) values (");
        appendValue(builder, data);
        builder.Append(", ");
        appendValue(builder, envelope.Id);
        builder.Append(", ");
        appendValue(builder, envelope.Status.ToString());
        builder.Append(", ");
        appendValue(builder, envelope.OwnerId);
        builder.Append(", ");
        appendValue(builder, envelope.ScheduledTime);
        builder.Append(", ");
        appendValue(builder, envelope.Attempts);
        builder.Append(", ");
        appendValue(builder, envelope.MessageType);
        builder.Append(", ");
        appendValue(builder, envelope.Destination?.ToString());
        builder.Append(", ");
        appendValue(builder, envelope.KeepUntil);
        builder.Append(");");
    }

    public static async Task<Envelope> ReadIncomingAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        var body = await reader.GetFieldValueAsync<byte[]>(0, cancellation);
        var envelope = body.Length > 0 ? EnvelopeSerializer.Deserialize(body) : new Envelope{Message = new PlaceHolder()};
        envelope.Id = await reader.GetFieldValueAsync<Guid>(1, cancellation);
        envelope.Status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(2, cancellation));
        envelope.OwnerId = await reader.GetFieldValueAsync<int>(3, cancellation);
        envelope.MessageType = await reader.GetFieldValueAsync<string>(6, cancellation);

        var rawUri = await reader.GetFieldValueAsync<string>(7, cancellation);
        envelope.Destination = new Uri(rawUri);

        if (!await reader.IsDBNullAsync(4, cancellation))
        {
            envelope.ScheduledTime = await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellation);
        }

        envelope.Attempts = await reader.GetFieldValueAsync<int>(5, cancellation);

        if (!await reader.IsDBNullAsync(8, cancellation))
        {
            envelope.KeepUntil = await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellation);
        }

        return envelope;
    }

    public static async Task<DeadLetterEnvelope> ReadDeadLetterAsync(DbDataReader reader,
        CancellationToken cancellation = default, ILogger? logger = null)
    {
        var id = await reader.GetFieldValueAsync<Guid>(0, cancellation);
        var executionTime = await reader.IsDBNullAsync(1, cancellation).ConfigureAwait(false) ? null : await reader.GetFieldValueAsync<DateTimeOffset?>(1, cancellation);

        // Read every scalar column BEFORE deserializing the body, so that a body we cannot read still
        // yields a row describing itself. See the placeholder note below.
        var messageType = await reader.GetFieldValueAsync<string>(3, cancellation);
        // GH-3166: received_at is written as envelope.Destination?.ToString() (DatabasePersistence's dead
        // letter insert), so it is NULL for any envelope that dead-lettered without a destination (e.g. a
        // locally-published message that failed before routing). Guard the read the same way `source` is —
        // an unguarded GetFieldValueAsync<string> throws on DBNull, which previously poisoned the whole
        // DLQ "Query Messages" fetch and surfaced as "No messages loaded".
        var receivedAt = await reader.IsDBNullAsync(4, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(4, cancellation);
        var source = await reader.IsDBNullAsync(5, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(5, cancellation);
        var exceptionType = await reader.IsDBNullAsync(6, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(6, cancellation);
        var exceptionMessage = await reader.IsDBNullAsync(7, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(7, cancellation);
        var sentAt = await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellation);
        var replayable = await reader.GetFieldValueAsync<bool>(9, cancellation);

        var envelope = await ReadDeadLetterBodyAsync(reader, id, messageType, receivedAt, cancellation, logger);

        var deadLetter = new DeadLetterEnvelope(
            id,
            executionTime,
            envelope,
            messageType,
            receivedAt,
            source,
            exceptionType,
            exceptionMessage,
            sentAt,
            replayable
        );

        await ReadDeadLetterExtendedFieldsAsync(reader, deadLetter, cancellation);

        return deadLetter;
    }

    /// <summary>
    /// GH-5209. Reads <see cref="DatabaseConstants.DeadLetterExtendedFields"/> when the query selected them
    /// right after <see cref="DatabaseConstants.DeadLetterFields"/>, as <see cref="DatabaseConstants.DeadLetterQueryFields"/>
    /// does. A reader over only the original fields is left alone.
    /// </summary>
    public static async Task ReadDeadLetterExtendedFieldsAsync(DbDataReader reader, DeadLetterEnvelope deadLetter,
        CancellationToken cancellation)
    {
        const int start = 10;
        if (reader.FieldCount < start + 7 ||
            !reader.GetName(start).Equals(DatabaseConstants.FailureGroup, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        deadLetter.FailureGroup = await readNullableStringAsync(reader, start, cancellation);
        deadLetter.FailureTitle = await readNullableStringAsync(reader, start + 1, cancellation);
        deadLetter.FailedAt = await ReadNullableTimestampAsync(reader, start + 2, cancellation);
        deadLetter.FirstFailedAt = await ReadNullableTimestampAsync(reader, start + 3, cancellation);
        deadLetter.ReplayCount = await reader.IsDBNullAsync(start + 4, cancellation)
            ? 0
            : Convert.ToInt32(reader.GetValue(start + 4));
        deadLetter.LastReplayedAt = await ReadNullableTimestampAsync(reader, start + 5, cancellation);
        deadLetter.ArchivedAt = await ReadNullableTimestampAsync(reader, start + 6, cancellation);
    }

    private static async Task<string?> readNullableStringAsync(DbDataReader reader, int ordinal,
        CancellationToken cancellation)
    {
        return await reader.IsDBNullAsync(ordinal, cancellation)
            ? null
            : await reader.GetFieldValueAsync<string>(ordinal, cancellation);
    }

    internal static async Task<DateTimeOffset?> ReadNullableTimestampAsync(DbDataReader reader, int ordinal,
        CancellationToken cancellation)
    {
        if (await reader.IsDBNullAsync(ordinal, cancellation)) return null;

        try
        {
            return await reader.GetFieldValueAsync<DateTimeOffset>(ordinal, cancellation);
        }
        catch (InvalidCastException)
        {
            return reader.GetValue(ordinal) switch
            {
                DateTimeOffset offset => offset,
                DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
                var other => DateTimeOffset.Parse(Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)!,
                    System.Globalization.CultureInfo.InvariantCulture)
            };
        }
    }

    // GH-3166 hardened the per-column reads; this hardens the body itself. A single unreadable `body`
    // — a row written by something other than EnvelopeSerializer (hand-seeded raw JSON is the observed
    // case), or one serialized by an incompatible Wolverine version — used to throw straight out of the
    // per-row read and abort the enclosing DeadLetterEnvelopeQuery for the WHOLE database. The console
    // then showed "No dead letter queue entries found", hiding every other, perfectly readable dead
    // letter in that store. One bad row must cost you that row, not the queue.
    // Public so the Oracle reader — which has to duplicate the surrounding column reads for RAW(16) Guids
    // and NUMBER(1) bools — shares this guard rather than re-growing its own copy of the bug.
    public static async Task<Envelope> ReadDeadLetterBodyAsync(DbDataReader reader, Guid id, string messageType,
        string receivedAt, CancellationToken cancellation, ILogger? logger = null)
    {
        try
        {
            if (await reader.IsDBNullAsync(2, cancellation))
            {
                return BuildUnreadableDeadLetterEnvelope(id, messageType, receivedAt);
            }

            return EnvelopeSerializer.Deserialize(await reader.GetFieldValueAsync<byte[]>(2, cancellation));
        }
        catch (Exception e)
        {
            logger?.LogError(e,
                "Unable to deserialize the stored body of dead letter envelope {Id} of message type {MessageType}. Returning a placeholder so the rest of the dead letter queue stays readable",
                id, messageType);

            return BuildUnreadableDeadLetterEnvelope(id, messageType, receivedAt);
        }
    }

    private static Envelope BuildUnreadableDeadLetterEnvelope(Guid id, string messageType, string receivedAt)
    {
        var envelope = new Envelope
        {
            Id = id,
            MessageType = messageType,
            Message = new PlaceHolder()
        };

        // received_at IS the destination Uri string (see the GH-3166 note above), so we can usually still
        // tell the operator which endpoint the poison row belongs to.
        if (receivedAt.IsNotEmpty() && Uri.TryCreate(receivedAt, UriKind.Absolute, out var destination))
        {
            envelope.Destination = destination;
        }

        return envelope;
    }

    public static void ConfigureDeadLetterCommands(DurabilitySettings durability, Envelope envelope,
        Exception? exception, DbCommandBuilder builder,
        IMessageDatabase wolverineDatabase)
    {
        // GH-5209. Fingerprint before serializing, so the failure-group header travels inside the stored envelope
        var fingerprint = DeadLetterFingerprinting.Apply(durability.DeadLetterFingerprinter, envelope, exception);

        byte[] data = [];
        try
        {
            data = EnvelopeSerializer.Serialize(envelope);
        }
        catch (WolverineSerializationException e)
        {
            wolverineDatabase.Logger.LogError(e, "Error trying to serialize a dead letter envelope");
        }

        var id = builder.AddParameter(envelope.Id);
        var now = builder.AddParameter(DateTimeOffset.UtcNow);

        var values = new List<string>
        {
            "@" + id.ParameterName,
            "@" + builder.AddParameter(envelope.ScheduledTime).ParameterName,
            "@" + builder.AddParameter(data).ParameterName,
            "@" + builder.AddParameter(envelope.MessageType ?? "unknown").ParameterName,
            "@" + builder.AddParameter(envelope.Destination?.ToString()).ParameterName,
            "@" + builder.AddParameter(envelope.Source ?? "unknown").ParameterName,
            "@" + builder.AddParameter(exception.DeadLetterExceptionType()).ParameterName,
            "@" + builder.AddParameter(exception.DeadLetterExceptionMessage()).ParameterName,
            "@" + builder.AddParameter(envelope.SentAt.ToUniversalTime()).ParameterName,
            "@" + builder.AddParameter(false).ParameterName,
            "@" + builder.AddParameter(fingerprint.Group).ParameterName,
            "@" + builder.AddParameter(fingerprint.Title).ParameterName,
            "@" + now.ParameterName
        };

        // GH-5209. A message that was replayed out of dead letter storage and failed again picks its replay
        // history back up from the history table the replay wrote
        var history = wolverineDatabase.TableNameFor(DatabaseConstants.DeadLetterHistoryTable);
        string historyOf(string column) =>
            $"(select h.{column} from {history} h where h.{DatabaseConstants.Id} = @{id.ParameterName})";

        values.Add($"coalesce({historyOf(DatabaseConstants.FirstFailedAt)}, @{now.ParameterName})");
        values.Add($"coalesce({historyOf(DatabaseConstants.ReplayCount)}, 0)");
        values.Add(historyOf(DatabaseConstants.LastReplayedAt));

        var deadLetterFields =
            $"{DatabaseConstants.DeadLetterFields}, {DatabaseConstants.FailureGroup}, {DatabaseConstants.FailureTitle}, {DatabaseConstants.FailedAt}, {DatabaseConstants.FirstFailedAt}, {DatabaseConstants.ReplayCount}, {DatabaseConstants.LastReplayedAt}";
        if (durability.DeadLetterQueueExpirationEnabled)
        {
            // If there is a deliver by, use that
            var expiration = envelope.DeliverBy.HasValue 
                ? builder.AddParameter(envelope.DeliverBy.Value)
                : builder.AddParameter(DateTimeOffset.UtcNow.Add(durability.DeadLetterQueueExpiration));
            
            values.Add("@" + expiration.ParameterName);
            deadLetterFields += ", " + DatabaseConstants.Expires;
        }

        builder.Append(
            $"insert into {wolverineDatabase.TableNameFor(DatabaseConstants.DeadLetterTable)} ({deadLetterFields}) values ({values.Join(", ")});");
    }

    /// <summary>
    /// GH-5209. The grouping key columns of a grouped dead letter summary. Rows recorded before failure
    /// fingerprinting have no failure group, and are grouped by exception type
    /// </summary>
    public static string DeadLetterGroupingColumns(DeadLetterGrouping grouping)
    {
        return grouping switch
        {
            DeadLetterGrouping.FailureGroup =>
                $"{DatabaseConstants.FailureGroup}, case when {DatabaseConstants.FailureGroup} is null then {DatabaseConstants.ExceptionType} end",
            DeadLetterGrouping.MessageType => DatabaseConstants.MessageType,
            DeadLetterGrouping.Endpoint => DatabaseConstants.ReceivedAt,
            DeadLetterGrouping.ExceptionType => DatabaseConstants.ExceptionType,
            _ => throw new ArgumentOutOfRangeException(nameof(grouping))
        };
    }

    /// <summary>
    /// GH-5209. How many columns <see cref="DeadLetterGroupingColumns"/> selects
    /// </summary>
    public static int DeadLetterGroupingColumnCount(DeadLetterGrouping grouping) =>
        grouping == DeadLetterGrouping.FailureGroup ? 2 : 1;

    /// <summary>
    /// GH-5209. The aggregates of a grouped dead letter summary, selected after the grouping columns and read in
    /// this order by <see cref="ReadDeadLetterGroupSummaryAsync"/>
    /// </summary>
    public static readonly string DeadLetterSummaryAggregates =
        $"max({DatabaseConstants.ExceptionType}), max({DatabaseConstants.FailureTitle}), count(*), " +
        $"min(coalesce({DatabaseConstants.FirstFailedAt}, {DatabaseConstants.FailedAt}, {DatabaseConstants.SentAt})), " +
        $"max(coalesce({DatabaseConstants.FailedAt}, {DatabaseConstants.SentAt}))";

    /// <summary>
    /// GH-5209. Reads one row of a grouped dead letter summary
    /// </summary>
    public static async Task<DeadLetterGroupSummary> ReadDeadLetterGroupSummaryAsync(DbDataReader reader,
        int offset, string serviceName, DeadLetterGrouping grouping, string? key, Uri database, CancellationToken token)
    {
        var exceptionType = await reader.IsDBNullAsync(offset, token)
            ? null
            : Convert.ToString(reader.GetValue(offset));
        var title = await reader.IsDBNullAsync(offset + 1, token)
            ? null
            : Convert.ToString(reader.GetValue(offset + 1));
        var count = Convert.ToInt32(reader.GetValue(offset + 2));

        var first = await ReadNullableTimestampAsync(reader, offset + 3, token);
        var last = await ReadNullableTimestampAsync(reader, offset + 4, token);

        var predates = grouping == DeadLetterGrouping.FailureGroup && key.IsEmpty();
        if (predates)
        {
            key = DeadLetterFingerprint.UnfingerprintedGroupFor(exceptionType);
            title = DeadLetterFingerprint.UnfingerprintedTitleFor(exceptionType);
        }
        else if (grouping == DeadLetterGrouping.Endpoint && key.IsEmpty())
        {
            key = UnknownReceivedAt;
        }

        return new DeadLetterGroupSummary(serviceName, database, grouping, key ?? string.Empty, count)
        {
            Title = grouping == DeadLetterGrouping.FailureGroup ? title ?? key : null,
            ExceptionType = grouping is DeadLetterGrouping.FailureGroup or DeadLetterGrouping.ExceptionType
                ? exceptionType
                : null,
            PredatesFingerprinting = predates,
            FirstFailedAt = first,
            LastFailedAt = last
        };
    }

    // GH-3166's sentinel for dead letters without a destination
    internal const string UnknownReceivedAt = "unknown://none";

    public static async Task<Envelope> ReadOutgoingAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        var body = await reader.GetFieldValueAsync<byte[]>(0, cancellation);
        var envelope = EnvelopeSerializer.Deserialize(body);
        envelope.OwnerId = await reader.GetFieldValueAsync<int>(2, cancellation);

        if (!await reader.IsDBNullAsync(4, cancellation))
        {
            envelope.DeliverBy = await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellation);
        }

        envelope.Attempts = await reader.GetFieldValueAsync<int>(5, cancellation);

        return envelope;
    }
}