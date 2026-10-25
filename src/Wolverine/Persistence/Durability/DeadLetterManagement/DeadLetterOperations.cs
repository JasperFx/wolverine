using JasperFx.Core;

namespace Wolverine.Persistence.Durability.DeadLetterManagement;

/// <summary>
/// The outcome of a bulk dead letter operation like <see cref="IDeadLetters.ReplayMatchingAsync"/>
/// </summary>
public record DeadLetterOperationResult
{
    /// <summary>
    /// A result from a store that does not report how many dead letters an operation affected
    /// </summary>
    public static readonly DeadLetterOperationResult NotReported = new();

    public DeadLetterOperationResult()
    {
    }

    public DeadLetterOperationResult(int? affected)
    {
        Affected = affected;
    }

    /// <summary>
    /// How many dead letters the operation affected. <c>null</c> means the store did not report it
    /// </summary>
    public int? Affected { get; init; }

    /// <summary>
    /// Combine the results of the same operation against several stores or batches. The total is only
    /// reported if every part reported it
    /// </summary>
    public DeadLetterOperationResult Add(DeadLetterOperationResult other)
    {
        return new DeadLetterOperationResult(Affected.HasValue && other.Affected.HasValue
            ? Affected.Value + other.Affected.Value
            : null);
    }
}

/// <summary>
/// The optional dead letter features an <see cref="IDeadLetters"/> implementation supports. Callers can
/// read <see cref="IDeadLetters.Capabilities"/> before offering a feature to an operator
/// </summary>
[Flags]
public enum DeadLetterCapabilities
{
    None = 0,

    /// <summary>
    /// Dead letters are fingerprinted into failure groups, and <see cref="DeadLetterEnvelopeQuery.FailureGroup"/>
    /// filters on them
    /// </summary>
    FailureGroups = 1,

    /// <summary>
    /// <see cref="IDeadLetters.ReplayMatchingAsync"/> and <see cref="IDeadLetters.DiscardMatchingAsync"/>
    /// report how many dead letters they affected
    /// </summary>
    OperationCounts = 2,

    /// <summary>
    /// <see cref="IDeadLetters.ArchiveMatchingAsync"/> and <see cref="IDeadLetters.RestoreMatchingAsync"/>
    /// are supported
    /// </summary>
    ArchiveAndRestore = 4,

    /// <summary>
    /// A dead letter that fails again after being replayed reports <see cref="DeadLetterEnvelope.ReplayCount"/>,
    /// <see cref="DeadLetterEnvelope.LastReplayedAt"/> and <see cref="DeadLetterEnvelope.FirstFailedAt"/>
    /// across its replays
    /// </summary>
    ReplayHistory = 8,

    /// <summary>
    /// <see cref="DeadLetterEnvelopeQuery.ExceptionMessageMatch"/> is honored
    /// </summary>
    ExceptionMessageMatching = 16,

    /// <summary>
    /// Everything above
    /// </summary>
    All = FailureGroups | OperationCounts | ArchiveAndRestore | ReplayHistory | ExceptionMessageMatching
}

/// <summary>
/// How <see cref="DeadLetterEnvelopeQuery.ExceptionMessage"/> is matched
/// </summary>
public enum DeadLetterMessageMatch
{
    /// <summary>
    /// The exception message must equal the value
    /// </summary>
    Exact,

    /// <summary>
    /// The exception message must contain the value. Wildcard characters in the value are matched literally
    /// </summary>
    Contains
}

/// <summary>
/// Which dead letters a query selects by their archived state
/// </summary>
public enum DeadLetterArchiveFilter
{
    /// <summary>
    /// Only dead letters that are not archived
    /// </summary>
    Exclude,

    /// <summary>
    /// Only archived dead letters
    /// </summary>
    Only,

    /// <summary>
    /// Archived or not
    /// </summary>
    Any
}

/// <summary>
/// How <see cref="IDeadLetters.SummarizeAllAsync(string, DeadLetterSummaryOptions, CancellationToken)"/>
/// groups dead letters
/// </summary>
public enum DeadLetterGrouping
{
    /// <summary>
    /// By failure group, see <see cref="IDeadLetterFingerprinter"/>
    /// </summary>
    FailureGroup,

    /// <summary>
    /// By message type
    /// </summary>
    MessageType,

    /// <summary>
    /// By the endpoint that received the message
    /// </summary>
    Endpoint,

    /// <summary>
    /// By exception type
    /// </summary>
    ExceptionType
}

/// <summary>
/// Options for <see cref="IDeadLetters.SummarizeAllAsync(string, DeadLetterSummaryOptions, CancellationToken)"/>
/// </summary>
public class DeadLetterSummaryOptions
{
    public DeadLetterGrouping Grouping { get; set; } = DeadLetterGrouping.FailureGroup;

    /// <summary>
    /// Restricts the summary to the dead letters this query matches. Paging is ignored. Archived dead
    /// letters are excluded unless <see cref="DeadLetterEnvelopeQuery.Archived"/> says otherwise
    /// </summary>
    public DeadLetterEnvelopeQuery Query { get; set; } = new();
}

/// <summary>
/// One group of dead letters from
/// <see cref="IDeadLetters.SummarizeAllAsync(string, DeadLetterSummaryOptions, CancellationToken)"/>
/// </summary>
/// <param name="ServiceName"></param>
/// <param name="Database">The store the dead letters are in</param>
/// <param name="Grouping">How the dead letters were grouped</param>
/// <param name="Key">The value of the grouping. For <see cref="DeadLetterGrouping.FailureGroup"/> this can be used
/// as <see cref="DeadLetterEnvelopeQuery.FailureGroup"/> to select the group</param>
/// <param name="Count">How many dead letters are in the group</param>
public record DeadLetterGroupSummary(
    string ServiceName,
    Uri? Database,
    DeadLetterGrouping Grouping,
    string Key,
    int Count)
{
    /// <summary>
    /// The title of the failure group, only for <see cref="DeadLetterGrouping.FailureGroup"/>
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// The exception type of the group, for <see cref="DeadLetterGrouping.FailureGroup"/> and
    /// <see cref="DeadLetterGrouping.ExceptionType"/>
    /// </summary>
    public string? ExceptionType { get; init; }

    /// <summary>
    /// True for the group of dead letters recorded before failure fingerprinting, which are grouped by
    /// exception type
    /// </summary>
    public bool PredatesFingerprinting { get; init; }

    /// <summary>
    /// When the earliest failure in the group happened, counting failures before a replay
    /// </summary>
    public DateTimeOffset? FirstFailedAt { get; init; }

    /// <summary>
    /// When the most recent failure in the group happened
    /// </summary>
    public DateTimeOffset? LastFailedAt { get; init; }
}

/// <summary>
/// Helpers shared by the <see cref="IDeadLetters"/> implementations
/// </summary>
public static class DeadLetterSummaries
{
    /// <summary>
    /// Group already loaded dead letters in memory
    /// </summary>
    public static IReadOnlyList<DeadLetterGroupSummary> Group(IEnumerable<DeadLetterEnvelope> envelopes,
        string serviceName, Uri? database, DeadLetterGrouping grouping)
    {
        return envelopes
            .GroupBy(x => KeyFor(x, grouping))
            .Select(group =>
            {
                var first = group.First();
                var predates = grouping == DeadLetterGrouping.FailureGroup && first.FailureGroup.IsEmpty();

                return new DeadLetterGroupSummary(serviceName, database, grouping, group.Key, group.Count())
                {
                    Title = grouping == DeadLetterGrouping.FailureGroup
                        ? predates
                            ? DeadLetterFingerprint.UnfingerprintedTitleFor(first.ExceptionType)
                            : group.Select(x => x.FailureTitle).FirstOrDefault(x => x.IsNotEmpty()) ?? group.Key
                        : null,
                    ExceptionType = grouping is DeadLetterGrouping.FailureGroup or DeadLetterGrouping.ExceptionType
                        ? first.ExceptionType
                        : null,
                    PredatesFingerprinting = predates,
                    FirstFailedAt = group.Min(x => x.FirstFailedAt ?? x.FailedAt ?? x.SentAt),
                    LastFailedAt = group.Max(x => x.FailedAt ?? x.SentAt)
                };
            })
            .OrderByDescending(x => x.Count)
            .ToList();
    }

    /// <summary>
    /// The value a dead letter is grouped by
    /// </summary>
    public static string KeyFor(DeadLetterEnvelope envelope, DeadLetterGrouping grouping)
    {
        return grouping switch
        {
            DeadLetterGrouping.FailureGroup => envelope.FailureGroup.IsNotEmpty()
                ? envelope.FailureGroup!
                : DeadLetterFingerprint.UnfingerprintedGroupFor(envelope.ExceptionType),
            DeadLetterGrouping.MessageType => envelope.MessageType,
            DeadLetterGrouping.Endpoint => envelope.ReceivedAt.IsNotEmpty() ? envelope.ReceivedAt : "unknown://none",
            DeadLetterGrouping.ExceptionType => envelope.ExceptionType,
            _ => throw new ArgumentOutOfRangeException(nameof(grouping))
        };
    }

    /// <summary>
    /// Summarize by reading every matching dead letter through <see cref="IDeadLetters.QueryAsync"/> and
    /// grouping in memory. This is the default for stores without a native implementation
    /// </summary>
    public static async Task<IReadOnlyList<DeadLetterGroupSummary>> SummarizeByQueryingAsync(IDeadLetters deadLetters,
        string serviceName, DeadLetterSummaryOptions options, CancellationToken token)
    {
        const int pageSize = 500;
        var all = new List<DeadLetterEnvelope>();
        Uri? database = null;

        var query = options.Query.CopyForPaging(pageSize);
        for (var page = 1;; page++)
        {
            query.PageNumber = page;
            var results = await deadLetters.QueryAsync(query, token);
            database ??= results.DatabaseUri;
            all.AddRange(results.Envelopes);

            if (results.Envelopes.Count < pageSize || all.Count >= results.TotalCount) break;
        }

        return Group(all, serviceName, database, options.Grouping);
    }

    /// <summary>
    /// Escape a value for a <c>LIKE ... ESCAPE '!'</c> "contains" match
    /// </summary>
    /// <param name="value"></param>
    /// <param name="escapeBrackets">SQL Server also treats [ as a wildcard. Oracle refuses an escape
    /// character in front of anything but a wildcard</param>
    public static string ContainsPattern(string value, bool escapeBrackets = true)
    {
        var escaped = value
            .Replace("!", "!!")
            .Replace("%", "!%")
            .Replace("_", "!_");

        if (escapeBrackets)
        {
            escaped = escaped.Replace("[", "![");
        }

        return "%" + escaped + "%";
    }

    /// <summary>
    /// The escape character used by <see cref="ContainsPattern"/>
    /// </summary>
    public const string LikeEscapeClause = " ESCAPE '!'";
}
