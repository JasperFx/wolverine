using NATS.Client.JetStream.Models;
using Wolverine.Nats.Configuration;

namespace Wolverine.Nats.Internal;

/// <summary>
/// What <see cref="NatsProvisioning"/> compares and reconciles. Only the settings Wolverine itself writes are
/// in scope: an update overlays them onto the configuration the server already has, so anything else on an
/// existing stream or consumer -- a description, metadata, placement, sources, a consumer's deliver policy --
/// stays as it is.
/// </summary>
internal static class JetStreamProvisioning
{
    /// <summary>
    /// The stream Wolverine creates for a stream declared through <c>DefineStream()</c> and friends
    /// </summary>
    public static StreamConfig BuildStreamConfig(string name, StreamConfiguration config, JetStreamDefaults defaults)
    {
        return new StreamConfig(name, config.Subjects)
        {
            Retention = config.Retention,
            Storage = config.Storage,
            MaxMsgs = config.MaxMessages ?? -1,
            MaxBytes = config.MaxBytes ?? -1,
            MaxAge = config.MaxAge ?? TimeSpan.Zero,
            MaxMsgsPerSubject = config.MaxMessagesPerSubject ?? 0,
            Discard = config.DiscardPolicy,
            NumReplicas = config.Replicas,
            DuplicateWindow = config.DuplicateWindow ?? defaults.DuplicateWindow,
            AllowRollupHdrs = config.AllowRollup,
            AllowDirect = config.AllowDirect,
            DenyDelete = config.DenyDelete,
            DenyPurge = config.DenyPurge,
            AllowMsgSchedules = config.AllowMsgSchedules
        };
    }

    /// <summary>
    /// Copy the settings Wolverine manages from <paramref name="desired"/> onto the server's
    /// <paramref name="existing"/> configuration, which is then what an update sends
    /// </summary>
    public static StreamConfig OverlayManagedSettings(StreamConfig existing, StreamConfig desired)
    {
        existing.Subjects = desired.Subjects;
        existing.Retention = desired.Retention;
        existing.Storage = desired.Storage;
        existing.MaxMsgs = desired.MaxMsgs;
        existing.MaxBytes = desired.MaxBytes;
        existing.MaxAge = desired.MaxAge;
        existing.MaxMsgsPerSubject = desired.MaxMsgsPerSubject;
        existing.Discard = desired.Discard;
        existing.NumReplicas = desired.NumReplicas;
        existing.DuplicateWindow = desired.DuplicateWindow;
        existing.AllowRollupHdrs = desired.AllowRollupHdrs;
        existing.AllowDirect = desired.AllowDirect;
        existing.DenyDelete = desired.DenyDelete;
        existing.DenyPurge = desired.DenyPurge;
        existing.AllowMsgSchedules = desired.AllowMsgSchedules;
        return existing;
    }

    /// <summary>
    /// Every managed stream setting where the server differs from <paramref name="desired"/>, as
    /// "Setting: configured X, server Y". Values the server stores in a different but equivalent form
    /// (0 vs -1 for "unlimited", an unset duplicate window) are not reported.
    /// </summary>
    public static IReadOnlyList<string> CompareStream(StreamConfig desired, StreamConfig actual)
    {
        var differences = new List<string>();

        compare(differences, "Subjects", describe(desired.Subjects), describe(actual.Subjects));
        compare(differences, nameof(StreamConfig.Retention), desired.Retention, actual.Retention);
        compare(differences, nameof(StreamConfig.Storage), desired.Storage, actual.Storage);
        compare(differences, nameof(StreamConfig.MaxMsgs), unlimited(desired.MaxMsgs), unlimited(actual.MaxMsgs));
        compare(differences, nameof(StreamConfig.MaxBytes), unlimited(desired.MaxBytes), unlimited(actual.MaxBytes));
        compare(differences, nameof(StreamConfig.MaxAge), desired.MaxAge, actual.MaxAge);
        compare(differences, nameof(StreamConfig.MaxMsgsPerSubject), unlimited(desired.MaxMsgsPerSubject),
            unlimited(actual.MaxMsgsPerSubject));
        compare(differences, nameof(StreamConfig.Discard), desired.Discard, actual.Discard);
        compare(differences, nameof(StreamConfig.NumReplicas), Math.Max(1, desired.NumReplicas),
            Math.Max(1, actual.NumReplicas));

        // The server substitutes its own default (2 minutes) for a zero window
        if (desired.DuplicateWindow > TimeSpan.Zero)
        {
            compare(differences, nameof(StreamConfig.DuplicateWindow), desired.DuplicateWindow, actual.DuplicateWindow);
        }

        // The server turns rollup headers on by itself for a stream with message schedules
        compare(differences, nameof(StreamConfig.AllowRollupHdrs), desired.AllowRollupHdrs || desired.AllowMsgSchedules,
            actual.AllowRollupHdrs);
        compare(differences, nameof(StreamConfig.AllowDirect), desired.AllowDirect, actual.AllowDirect);
        compare(differences, nameof(StreamConfig.DenyDelete), desired.DenyDelete, actual.DenyDelete);
        compare(differences, nameof(StreamConfig.DenyPurge), desired.DenyPurge, actual.DenyPurge);
        compare(differences, nameof(StreamConfig.AllowMsgSchedules), desired.AllowMsgSchedules,
            actual.AllowMsgSchedules);

        return differences;
    }

    /// <summary>
    /// Copy the consumer settings Wolverine manages from <paramref name="desired"/> onto the server's
    /// <paramref name="existing"/> configuration, which is then what an update sends. The same set resource
    /// setup writes through <see cref="NatsEndpoint.ApplyManagedConsumerSettings"/>, plus the filter.
    /// </summary>
    public static ConsumerConfig OverlayManagedSettings(ConsumerConfig existing, ConsumerConfig desired)
    {
        existing.AckPolicy = desired.AckPolicy;
        existing.AckWait = desired.AckWait;
        existing.MaxDeliver = desired.MaxDeliver;

        // Unset means "leave the server's value alone": Wolverine only sizes it for NativeAck or on request
        if (desired.MaxAckPending > 0)
        {
            existing.MaxAckPending = desired.MaxAckPending;
        }

        existing.FilterSubject = desired.FilterSubject;
        existing.FilterSubjects = desired.FilterSubjects;
        return existing;
    }

    /// <summary>
    /// Every managed consumer setting where the server differs from <paramref name="desired"/>
    /// </summary>
    public static IReadOnlyList<string> CompareConsumer(ConsumerConfig desired, ConsumerConfig actual)
    {
        var differences = new List<string>();

        compare(differences, nameof(ConsumerConfig.AckPolicy), desired.AckPolicy, actual.AckPolicy);
        compare(differences, nameof(ConsumerConfig.AckWait), desired.AckWait, actual.AckWait);
        compare(differences, nameof(ConsumerConfig.MaxDeliver), desired.MaxDeliver, actual.MaxDeliver);

        if (desired.MaxAckPending > 0)
        {
            compare(differences, nameof(ConsumerConfig.MaxAckPending), desired.MaxAckPending, actual.MaxAckPending);
        }

        compare(differences, "FilterSubjects", describe(filters(desired)), describe(filters(actual)));

        return differences;
    }

    private static IEnumerable<string> filters(ConsumerConfig config)
    {
        if (config.FilterSubjects is { Count: > 0 } many)
        {
            return many;
        }

        return string.IsNullOrEmpty(config.FilterSubject) ? [] : [config.FilterSubject];
    }

    private static long unlimited(long value) => value <= 0 ? -1 : value;

    private static string describe(IEnumerable<string>? subjects)
    {
        var sorted = (subjects ?? []).Order(StringComparer.Ordinal).ToArray();
        return sorted.Length == 0 ? "(none)" : string.Join(", ", sorted);
    }

    private static void compare<T>(List<string> differences, string setting, T configured, T server)
    {
        if (!EqualityComparer<T>.Default.Equals(configured, server))
        {
            differences.Add($"{setting}: configured {configured}, server {server}");
        }
    }
}
