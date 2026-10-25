using System.Security.Cryptography;
using System.Text;
using Wolverine.Transports;

namespace Wolverine.Persistence.Durability.DeadLetterManagement;

/// <summary>
/// The failure group a dead letter belongs to: a stable key the dead letter storage can group,
/// filter, replay or discard on, and a human readable title for that group
/// </summary>
/// <param name="Group">Stable, opaque key of the failure group</param>
/// <param name="Title">Human readable description of the failure group</param>
public record DeadLetterFingerprint(string Group, string Title)
{
    /// <summary>
    /// Maximum length of a persisted failure group key
    /// </summary>
    public const int MaxGroupLength = 100;

    /// <summary>
    /// Maximum length of a persisted failure title
    /// </summary>
    public const int MaxTitleLength = 500;

    /// <summary>
    /// Dead letters persisted before failure fingerprinting existed have no failure group. Summaries
    /// report them in a group keyed by this prefix plus the exception type, and the same key can be used
    /// as <see cref="DeadLetterEnvelopeQuery.FailureGroup"/> to select them again
    /// </summary>
    public const string UnfingerprintedGroupPrefix = "unfingerprinted:";

    /// <summary>
    /// The failure group key used for dead letters written before failure fingerprinting
    /// </summary>
    public static string UnfingerprintedGroupFor(string? exceptionType) =>
        UnfingerprintedGroupPrefix + (exceptionType ?? string.Empty);

    /// <summary>
    /// The title used for the group of dead letters written before failure fingerprinting
    /// </summary>
    public static string UnfingerprintedTitleFor(string? exceptionType) =>
        $"{(string.IsNullOrEmpty(exceptionType) ? "Unknown" : exceptionType)} (recorded before failure fingerprinting)";

    /// <summary>
    /// Is this failure group key the one used for dead letters written before failure fingerprinting?
    /// </summary>
    public static bool TryParseUnfingerprintedGroup(string? group, out string exceptionType)
    {
        if (group != null && group.StartsWith(UnfingerprintedGroupPrefix, StringComparison.Ordinal))
        {
            exceptionType = group[UnfingerprintedGroupPrefix.Length..];
            return true;
        }

        exceptionType = string.Empty;
        return false;
    }

    /// <summary>
    /// Builds a fingerprint whose group is a deterministic hash of <paramref name="identity"/>
    /// </summary>
    public static DeadLetterFingerprint Hashed(string identity, string title)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

        // 128 bits is plenty to keep distinct failures apart, and keeps the indexed column narrow
        return new DeadLetterFingerprint(Convert.ToHexStringLower(hash, 0, 16), title);
    }

    internal DeadLetterFingerprint Truncated()
    {
        var group = Group.Length > MaxGroupLength ? Group[..MaxGroupLength] : Group;
        var title = Title.Length > MaxTitleLength ? Title[..MaxTitleLength] : Title;
        return group == Group && title == Title ? this : new DeadLetterFingerprint(group, title);
    }
}

/// <summary>
/// Computes the failure group of a message as it is moved to dead letter storage, so the dead letter
/// storage can manage dead letters as groups of the same failure. Register an implementation in the
/// application's container, or set <see cref="DurabilitySettings.DeadLetterFingerprinter"/>, to replace
/// <see cref="DefaultDeadLetterFingerprinter"/>
/// </summary>
public interface IDeadLetterFingerprinter
{
    /// <summary>
    /// Compute the failure group of a message being dead lettered
    /// </summary>
    /// <param name="envelope">The envelope being dead lettered</param>
    /// <param name="exception">The failure, if there is one</param>
    /// <returns></returns>
    DeadLetterFingerprint Fingerprint(Envelope envelope, Exception? exception);
}

/// <summary>
/// The default <see cref="IDeadLetterFingerprinter"/>. The failure group is a deterministic hash of the
/// exception type and the first stack frame from application code, so the same bug lands in one group
/// whatever data was in the exception message, and two different bugs that throw the same exception type
/// land in different groups
/// </summary>
public class DefaultDeadLetterFingerprinter : IDeadLetterFingerprinter
{
    /// <summary>
    /// Stack frames whose method starts with one of these prefixes are treated as framework code and
    /// skipped when looking for the first frame from application code. Add your own shared
    /// infrastructure namespaces here
    /// </summary>
    public List<string> FrameworkNamespacePrefixes { get; } =
    [
        "System.",
        "Microsoft.",
        "Npgsql.",
        "MySqlConnector.",
        "Oracle.",
        "Wolverine.",
        "JasperFx.",
        "Weasel.",
        "Marten.",
        "Polecat.",
        "Fisher.",
        "Lamar.",
        // Wolverine's generated handler code
        "Internal.Generated.",
        "lambda_method"
    ];

    public DeadLetterFingerprint Fingerprint(Envelope envelope, Exception? exception)
    {
        var exceptionType = exception.DeadLetterExceptionType() ?? "Unknown";
        var frame = FindApplicationFrame(exception?.StackTrace, exception?.Message);

        if (frame == null)
        {
            var noStack = $"{exceptionType}: no stack trace";
            return DeadLetterFingerprint.Hashed(noStack, noStack);
        }

        return DeadLetterFingerprint.Hashed($"{exceptionType}|{frame}", $"{exceptionType}: {TitleFor(frame)}");
    }

    /// <summary>
    /// Find the first stack frame from application code, or the first frame at all if no frame qualifies.
    /// The returned frame is the method signature only, without the file name or line number, so it stays
    /// stable across builds
    /// </summary>
    /// <param name="stackTrace">The stack trace text</param>
    /// <param name="exceptionMessage">The exception message, removed from the stack trace text before
    /// parsing so that data in the message never splits a failure group</param>
    /// <returns></returns>
    public string? FindApplicationFrame(string? stackTrace, string? exceptionMessage = null)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return null;

        if (!string.IsNullOrEmpty(exceptionMessage))
        {
            stackTrace = stackTrace.Replace(exceptionMessage, string.Empty, StringComparison.Ordinal);
        }

        string? first = null;
        foreach (var line in stackTrace.Split('\n'))
        {
            var frame = ParseFrame(line);
            if (frame == null) continue;

            first ??= frame;
            if (!IsFrameworkFrame(frame)) return frame;
        }

        return first;
    }

    /// <summary>
    /// Is this frame from framework code?
    /// </summary>
    public bool IsFrameworkFrame(string frame)
    {
        foreach (var prefix in FrameworkNamespacePrefixes)
        {
            if (frame.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    // "   at Namespace.Type.Method(Args) in /path/File.cs:line 12" => "Namespace.Type.Method(Args)"
    internal static string? ParseFrame(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("at ", StringComparison.Ordinal)) return null;

        var frame = trimmed[3..];
        var location = frame.IndexOf(") in ", StringComparison.Ordinal);
        if (location >= 0)
        {
            frame = frame[..(location + 1)];
        }

        frame = frame.Trim();
        return frame.Length == 0 ? null : frame;
    }

    // "Namespace.Type.Method(String id)" => "Type.Method(...)"
    internal static string TitleFor(string frame)
    {
        var paren = frame.IndexOf('(');
        var method = paren >= 0 ? frame[..paren] : frame;

        var lastDot = method.LastIndexOf('.');
        var typeDot = lastDot > 0 ? method.LastIndexOf('.', lastDot - 1) : -1;
        var shortened = typeDot >= 0 ? method[(typeDot + 1)..] : method;

        return paren >= 0 ? shortened + "(...)" : shortened;
    }
}

/// <summary>
/// Applies failure fingerprinting as a message is moved to dead letter storage
/// </summary>
public static class DeadLetterFingerprinting
{
    /// <summary>
    /// Resolve the failure group of a message being dead lettered, and stamp it onto the envelope's
    /// <see cref="DeadLetterQueueConstants.FailureGroupHeader"/> and
    /// <see cref="DeadLetterQueueConstants.FailureTitleHeader"/> headers so it travels with the envelope.
    /// <para>
    /// When the exception carries no stack trace -- a dead letter recovered from a broker's native dead
    /// letter queue, or an exception substituted by an <see cref="IDeadLetterInterceptor"/> -- a failure
    /// group already stamped on the envelope where the original exception was caught is kept. Otherwise the
    /// fingerprinter computes it from the exception.
    /// </para>
    /// </summary>
    public static DeadLetterFingerprint Apply(IDeadLetterFingerprinter? fingerprinter, Envelope envelope,
        Exception? exception)
    {
        if (exception?.StackTrace == null &&
            envelope.TryGetHeader(DeadLetterQueueConstants.FailureGroupHeader, out var group) &&
            !string.IsNullOrEmpty(group))
        {
            envelope.TryGetHeader(DeadLetterQueueConstants.FailureTitleHeader, out var title);
            return new DeadLetterFingerprint(group, title ?? group).Truncated();
        }

        DeadLetterFingerprint fingerprint;
        try
        {
            fingerprint = (fingerprinter ?? Default).Fingerprint(envelope, exception);
        }
        catch (Exception)
        {
            // A failing custom fingerprinter must never cost us the dead letter itself
            fingerprint = Default.Fingerprint(envelope, exception);
        }

        fingerprint = fingerprint.Truncated();

        envelope.Headers[DeadLetterQueueConstants.FailureGroupHeader] = fingerprint.Group;
        envelope.Headers[DeadLetterQueueConstants.FailureTitleHeader] = fingerprint.Title;

        return fingerprint;
    }

    /// <summary>
    /// The default fingerprinter, used when nothing else is configured
    /// </summary>
    public static readonly IDeadLetterFingerprinter Default = new DefaultDeadLetterFingerprinter();
}
