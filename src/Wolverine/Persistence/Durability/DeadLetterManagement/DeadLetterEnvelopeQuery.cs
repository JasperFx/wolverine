using JasperFx.Core;

namespace Wolverine.Persistence.Durability.DeadLetterManagement;

public class DeadLetterEnvelopeQuery
{
    public DeadLetterEnvelopeQuery(TimeRange range)
    {
        Range = range;
    }

    public DeadLetterEnvelopeQuery(Guid[] messageIds)
    {
        MessageIds = messageIds;
    }

    public DeadLetterEnvelopeQuery()
    {
    }

    public int PageNumber { get; set; }
    public int PageSize { get; set; } = 100;

    public string? MessageType { get; set; }
    public string? ExceptionType { get; set; }
    public string? ReceivedAt { get; set; }
    
    public string? ExceptionMessage { get; set; }

    /// <summary>
    /// How <see cref="ExceptionMessage"/> is matched. <c>null</c> (the default) keeps the original behavior:
    /// the value is used as a SQL <c>LIKE</c> pattern on the relational stores, which is an exact match unless
    /// the value contains wildcards
    /// </summary>
    public DeadLetterMessageMatch? ExceptionMessageMatch { get; set; }

    /// <summary>
    /// When set, restricts results to the dead letters in one failure group. See <see cref="IDeadLetterFingerprinter"/>.
    /// A key starting with <see cref="DeadLetterFingerprint.UnfingerprintedGroupPrefix"/> selects the dead
    /// letters of that exception type recorded before failure fingerprinting
    /// </summary>
    public string? FailureGroup { get; set; }

    /// <summary>
    /// When set, scopes the query to one tenant. Dead letters are stored per database, so for a multi-tenanted
    /// message store this routes the query to the tenant's database. A message store that serves a single
    /// database ignores it
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Which dead letters to select by archived state. <c>null</c> (the default) excludes archived dead letters,
    /// except when <see cref="MessageIds"/> names the dead letters explicitly. Replay never touches an archived
    /// dead letter: restore it first
    /// </summary>
    public DeadLetterArchiveFilter? Archived { get; set; }

    /// <summary>
    /// The archived state filter this query applies, resolving the default
    /// </summary>
    /// <returns></returns>
    public DeadLetterArchiveFilter EffectiveArchiveFilter()
    {
        if (Archived.HasValue) return Archived.Value;
        return MessageIds is { Length: > 0 } ? DeadLetterArchiveFilter.Any : DeadLetterArchiveFilter.Exclude;
    }

    /// <summary>
    /// A copy of this query's criteria with different paging, for reading every match one page at a time
    /// </summary>
    public DeadLetterEnvelopeQuery CopyForPaging(int pageSize)
    {
        return new DeadLetterEnvelopeQuery
        {
            PageNumber = 1,
            PageSize = pageSize,
            MessageType = MessageType,
            ExceptionType = ExceptionType,
            ReceivedAt = ReceivedAt,
            ExceptionMessage = ExceptionMessage,
            ExceptionMessageMatch = ExceptionMessageMatch,
            FailureGroup = FailureGroup,
            TenantId = TenantId,
            Archived = Archived,
            Replayable = Replayable,
            Range = Range,
            MessageIds = MessageIds,
            Headers = Headers == null ? null : new Dictionary<string, string>(Headers),
            QueryId = QueryId
        };
    }

    /// <summary>
    /// Does this dead letter satisfy the <see cref="ExceptionMessage"/> criteria, using
    /// <see cref="ExceptionMessageMatch"/>? For stores that match in memory. A <c>null</c> match mode is
    /// treated as a "starts with" match there
    /// </summary>
    public bool MatchesExceptionMessage(string? exceptionMessage)
    {
        if (string.IsNullOrEmpty(ExceptionMessage)) return true;
        if (exceptionMessage == null) return false;

        return ExceptionMessageMatch switch
        {
            DeadLetterMessageMatch.Exact => string.Equals(exceptionMessage, ExceptionMessage, StringComparison.Ordinal),
            DeadLetterMessageMatch.Contains => exceptionMessage.Contains(ExceptionMessage, StringComparison.Ordinal),
            _ => exceptionMessage.StartsWith(ExceptionMessage, StringComparison.Ordinal)
        };
    }

    /// <summary>
    /// Does this dead letter satisfy the <see cref="FailureGroup"/> criteria? For stores that match in memory
    /// </summary>
    public bool MatchesFailureGroup(string? failureGroup, string? exceptionType)
    {
        if (string.IsNullOrEmpty(FailureGroup)) return true;

        if (DeadLetterFingerprint.TryParseUnfingerprintedGroup(FailureGroup, out var legacyType))
        {
            return string.IsNullOrEmpty(failureGroup) && string.Equals(exceptionType ?? string.Empty, legacyType, StringComparison.Ordinal);
        }

        return string.Equals(failureGroup, FailureGroup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Does a dead letter with this archived timestamp satisfy the archived state criteria?
    /// </summary>
    public bool MatchesArchived(DateTimeOffset? archivedAt)
    {
        return EffectiveArchiveFilter() switch
        {
            DeadLetterArchiveFilter.Exclude => archivedAt == null,
            DeadLetterArchiveFilter.Only => archivedAt != null,
            _ => true
        };
    }

    /// <summary>
    /// When set, restricts results to envelopes whose replayable flag matches. <c>null</c>
    /// (the default) keeps the current behavior of not filtering on the replayable column.
    /// <c>false</c> returns only envelopes not yet marked replayable; <c>true</c> returns only
    /// those already marked for replay.
    /// </summary>
    public bool? Replayable { get; set; }

    public TimeRange Range { get; set; } = TimeRange.AllTime();

    /// <summary>
    /// If set, this takes precedence over all other options
    /// </summary>
    public Guid[] MessageIds { get; set; } = [];

    /// <summary>
    /// When set, restricts results to envelopes that carry every one of these headers with exactly
    /// the given value (ordinal, case-sensitive comparison of the value). <c>null</c> or empty (the
    /// default) does not filter on headers at all. This applies to <see cref="IDeadLetters.QueryAsync"/>
    /// and equally to the bulk <see cref="IDeadLetters.ReplayAsync"/> and <see cref="IDeadLetters.DiscardAsync"/>
    /// operations. For example, every occurrence of a recurring schedule carries the
    /// <see cref="RecurringMessage.HeaderKey"/> header, so
    /// <c>new DeadLetterEnvelopeQuery().Header(RecurringMessage.HeaderKey, "nightly-report")</c> finds the
    /// dead letters produced by that schedule.
    /// <para>
    /// The dead letter stores keep envelope headers inside the serialized envelope body rather than in
    /// a queryable column, so this filter is applied after the other criteria by reading and
    /// deserializing every candidate row. Narrow the query with <see cref="MessageType"/> or
    /// <see cref="Range"/> where you can.
    /// </para>
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Fluent helper to require that matching envelopes carry the header <paramref name="name"/>
    /// with exactly <paramref name="value"/>. Can be called more than once to require several headers.
    /// </summary>
    /// <param name="name"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    public DeadLetterEnvelopeQuery Header(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        Headers ??= new Dictionary<string, string>();
        Headers[name] = value;
        return this;
    }

    /// <summary>
    /// Is there any header criteria on this query?
    /// </summary>
    /// <returns></returns>
    public bool HasHeaderFilter() => Headers is { Count: > 0 };

    /// <summary>
    /// Does this envelope satisfy the <see cref="Headers"/> criteria? Always true when there
    /// is no header criteria.
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    public bool MatchesHeaders(Envelope? envelope)
    {
        if (!HasHeaderFilter()) return true;
        if (envelope == null || !envelope.HasHeaders) return false;

        foreach (var pair in Headers!)
        {
            if (!envelope.Headers.TryGetValue(pair.Key, out var actual)) return false;
            if (!string.Equals(actual, pair.Value, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    /// <summary>
    /// Purely a marker for request/response scenarios
    /// </summary>
    public Guid QueryId { get; set; } = Guid.NewGuid();
}