using JasperFx.Descriptors;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Wolverine.Nats.Configuration;

public class NatsTransportConfiguration
{
    // GH-3269: the raw connection string may embed userinfo (nats://user:pass@host), and the auth properties below are
    // secrets. They are suppressed from the reflected diagnostic tree; the sanitized host:port target is surfaced via
    // NatsTransport.DescribeEndpoint() instead.
    [IgnoreDescription]
    public string ConnectionString { get; set; } = "nats://localhost:4222";
    public string? ClientName { get; set; }
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    [IgnoreDescription]
    public string? Username { get; set; }
    [IgnoreDescription]
    public string? Password { get; set; }
    [IgnoreDescription]
    public string? Token { get; set; }
    [IgnoreDescription]
    public string? Jwt { get; set; }
    [IgnoreDescription]
    public string? NKeySeed { get; set; }
    [IgnoreDescription]
    public string? CredentialsFile { get; set; }
    [IgnoreDescription]
    public string? NKeyFile { get; set; }
    [IgnoreDescription]
    public Func<Uri, CancellationToken, ValueTask<NatsAuthCred>>? AuthCallback { get; set; }

    public bool EnableTls { get; set; }
    public TlsMode TlsMode { get; set; } = TlsMode.Auto;
    public bool TlsInsecure { get; set; }
    public string? ClientCertFile { get; set; }
    public string? ClientKeyFile { get; set; }
    public string? CaFile { get; set; }

    public bool EnableJetStream { get; set; } = true;
    public string? JetStreamDomain { get; set; }
    public string? JetStreamApiPrefix { get; set; }

    public bool AutoProvision { get; set; } = true;

    /// <summary>
    /// What startup does with declared streams and named listener consumers that already exist on the server.
    /// When null (the default), existing streams are left alone (<see cref="NatsProvisioning.CreateOnly"/>) -- an
    /// update can discard data -- while named consumers are brought in line with the configuration
    /// (<see cref="NatsProvisioning.CreateOrUpdate"/>), so a changed <c>MaxDeliver</c> or <c>AckWait</c> just works.
    /// </summary>
    public NatsProvisioning? Provisioning { get; set; }

    /// <summary>
    /// <see cref="Provisioning"/> as it applies to declared streams
    /// </summary>
    internal NatsProvisioning StreamProvisioning => Provisioning ?? NatsProvisioning.CreateOnly;

    /// <summary>
    /// <see cref="Provisioning"/> as it applies to the named consumers of JetStream listeners
    /// </summary>
    internal NatsProvisioning ConsumerProvisioning => Provisioning ?? NatsProvisioning.CreateOrUpdate;
    public string? DefaultQueueGroup { get; set; }
    public bool NormalizeSubjects { get; set; } = true;
    public JetStreamDefaults JetStreamDefaults { get; set; } = new();

    /// <summary>
    /// Whether a tenant-aware listener takes the tenant id of an incoming message from its <c>tenant-id</c> header
    /// (the default). Set it to false when producers you do not control can publish to the listener's subjects:
    /// a message on a tenant's own connection then belongs to that tenant, a message on the shared connection to
    /// the configured tenant its subject prefix names, and any other message to no tenant, whatever its header
    /// says. Only tenant-aware listeners of a transport with tenants (<c>AddTenant()</c>) have anything to take the
    /// tenant from instead; everywhere else the header is used as before.
    /// </summary>
    public bool TrustTenantIdHeader { get; set; } = true;

    public ITenantIdResolver? TenantIdResolver { get; set; }
    public ISubjectResolver? SubjectResolver { get; set; }
    public string? TenantSubjectPrefix { get; set; }

    /// <summary>
    /// Optional source of the JetStream <c>Nats-Msg-Id</c> used for server-side
    /// deduplication (within the stream's duplicate window). Defaults to the Wolverine
    /// envelope Id when null; set it to project a domain identity such as
    /// <c>{stream}/{version}</c> so non-Wolverine consumers get server-side dedup too.
    /// An explicit <c>Nats-Msg-Id</c> header already on the outgoing envelope wins.
    /// </summary>
    [IgnoreDescription]
    public Func<Envelope, string>? MsgIdSource { get; set; }

    /// <summary>
    /// GH-4860. Optional last word over the <see cref="NatsJSPubOpts"/> of every JetStream publish: it receives
    /// the envelope and the options Wolverine built -- the <c>Nats-Msg-Id</c> already resolved -- and the publish
    /// uses whatever it returns. Reach for it for the publish options Wolverine does not surface itself, such as
    /// the <c>Nats-Expected-*</c> checks (<see cref="NatsJSPubOpts.ExpectedStream"/>,
    /// <see cref="NatsJSPubOpts.ExpectedLastSequence"/>, <see cref="NatsJSPubOpts.ExpectedLastSubjectSequence"/>,
    /// <see cref="NatsJSPubOpts.ExpectedLastMsgId"/>) or a per-publish timeout. Null (the default) leaves the
    /// options as Wolverine built them.
    /// </summary>
    [IgnoreDescription]
    public Func<Envelope, NatsJSPubOpts, NatsJSPubOpts>? ConfigureJetStreamPublish { get; set; }

    /// <summary>
    /// Optional last word over the NATS.Net <see cref="NatsOpts"/> of a connection the transport opens: it receives
    /// the options Wolverine built from this configuration -- connection already named -- and the connection uses
    /// whatever it returns. Reach for it for client settings this configuration does not surface, such as
    /// <see cref="NatsOpts.SubPendingChannelCapacity"/>, <see cref="NatsOpts.SubPendingChannelFullMode"/>,
    /// <see cref="NatsOpts.PingInterval"/> or the reconnect behavior. Applies to the shared connection and to every
    /// tenant's dedicated connection; a tenant's own configuration may set one of its own, which then replaces the
    /// transport's for that tenant. Null (the default) leaves the NATS.Net defaults alone.
    /// </summary>
    [IgnoreDescription]
    public Func<NatsOpts, NatsOpts>? ConfigureNatsOpts { get; set; }

    public Dictionary<string, StreamConfiguration> Streams { get; set; } = new();

    /// <summary>
    /// A copy to seed a tenant's connection configuration with, so a tenant action only overrides what differs
    /// (mirrors RabbitMqTenant copying the parent's connection factory when it connects)
    /// </summary>
    internal NatsTransportConfiguration CopyForTenant()
    {
        var clone = new NatsTransportConfiguration();
        foreach (var property in typeof(NatsTransportConfiguration).GetProperties())
        {
            if (property is { CanRead: true, CanWrite: true })
            {
                property.SetValue(clone, property.GetValue(this));
            }
        }

        // The reflective copy above aliases the two mutable reference members by reference, so a tenant action
        // that mutates them in place (e.g. cfg.JetStreamDefaults.DuplicateWindow = ... or cfg.Streams[...] = ...)
        // would leak into the shared transport config and every other tenant. Give each tenant its own copy.
        clone.JetStreamDefaults = copy(JetStreamDefaults);
        clone.Streams = Streams.ToDictionary(pair => pair.Key, pair => copy(pair.Value));

        return clone;
    }

    /// <summary>
    /// The settings a tenant's configuration offers that only ever apply transport-wide, where
    /// <paramref name="tenant"/> differs from this configuration
    /// </summary>
    internal IReadOnlyList<string> TransportWideSettingsChangedBy(NatsTransportConfiguration tenant)
    {
        var changed = new List<string>();

        void check(string name, object? transportValue, object? tenantValue)
        {
            if (!Equals(transportValue, tenantValue))
            {
                changed.Add(name);
            }
        }

        check(nameof(EnableJetStream), EnableJetStream, tenant.EnableJetStream);
        check(nameof(DefaultQueueGroup), DefaultQueueGroup, tenant.DefaultQueueGroup);
        check(nameof(NormalizeSubjects), NormalizeSubjects, tenant.NormalizeSubjects);
        check(nameof(MsgIdSource), MsgIdSource, tenant.MsgIdSource);
        check(nameof(TenantIdResolver), TenantIdResolver, tenant.TenantIdResolver);
        check(nameof(SubjectResolver), SubjectResolver, tenant.SubjectResolver);
        check(nameof(TenantSubjectPrefix), TenantSubjectPrefix, tenant.TenantSubjectPrefix);
        check(nameof(TrustTenantIdHeader), TrustTenantIdHeader, tenant.TrustTenantIdHeader);
        check(nameof(ConfigureJetStreamPublish), ConfigureJetStreamPublish, tenant.ConfigureJetStreamPublish);

        // The consumers of listeners on the tenant's connection and the streams resource setup creates for an
        // endpoint follow the transport
        check($"{nameof(JetStreamDefaults)}.{nameof(JetStreamDefaults.AckWait)}", JetStreamDefaults.AckWait,
            tenant.JetStreamDefaults.AckWait);
        check($"{nameof(JetStreamDefaults)}.{nameof(JetStreamDefaults.MaxDeliver)}", JetStreamDefaults.MaxDeliver,
            tenant.JetStreamDefaults.MaxDeliver);
        check($"{nameof(JetStreamDefaults)}.{nameof(JetStreamDefaults.DeliverPolicy)}",
            JetStreamDefaults.DeliverPolicy, tenant.JetStreamDefaults.DeliverPolicy);
        check($"{nameof(JetStreamDefaults)}.{nameof(JetStreamDefaults.MaxAge)}", JetStreamDefaults.MaxAge,
            tenant.JetStreamDefaults.MaxAge);
        check($"{nameof(JetStreamDefaults)}.{nameof(JetStreamDefaults.MaxMessages)}",
            JetStreamDefaults.MaxMessages, tenant.JetStreamDefaults.MaxMessages);
        check($"{nameof(JetStreamDefaults)}.{nameof(JetStreamDefaults.MaxBytes)}", JetStreamDefaults.MaxBytes,
            tenant.JetStreamDefaults.MaxBytes);
        check($"{nameof(JetStreamDefaults)}.{nameof(JetStreamDefaults.Replicas)}", JetStreamDefaults.Replicas,
            tenant.JetStreamDefaults.Replicas);

        return changed;
    }

    private static JetStreamDefaults copy(JetStreamDefaults source)
    {
        return new JetStreamDefaults
        {
            MaxAge = source.MaxAge,
            MaxMessages = source.MaxMessages,
            MaxBytes = source.MaxBytes,
            Replicas = source.Replicas,
            AckWait = source.AckWait,
            MaxDeliver = source.MaxDeliver,
            DuplicateWindow = source.DuplicateWindow,
            DeliverPolicy = source.DeliverPolicy
        };
    }

    private static StreamConfiguration copy(StreamConfiguration source)
    {
        return new StreamConfiguration
        {
            Name = source.Name,
            Subjects = new List<string>(source.Subjects),
            Retention = source.Retention,
            Storage = source.Storage,
            MaxMessages = source.MaxMessages,
            MaxBytes = source.MaxBytes,
            MaxAge = source.MaxAge,
            MaxMessagesPerSubject = source.MaxMessagesPerSubject,
            DiscardPolicy = source.DiscardPolicy,
            Replicas = source.Replicas,
            AllowRollup = source.AllowRollup,
            AllowDirect = source.AllowDirect,
            DenyDelete = source.DenyDelete,
            DenyPurge = source.DenyPurge,
            DuplicateWindow = source.DuplicateWindow,
            AllowMsgSchedules = source.AllowMsgSchedules,
            DeclaredByShardTopology = source.DeclaredByShardTopology,
            Mirror = source.Mirror == null ? null : copy(source.Mirror),
            Sources = source.Sources.Select(copy).ToList(),
            Placement = source.Placement == null
                ? null
                : source.Placement with { Tags = source.Placement.Tags?.ToList() }
        };
    }

    // The NATS.Net models are records, but with nested objects a tenant action could still change in place
    private static StreamSource copy(StreamSource source)
    {
        return source with
        {
            External = source.External == null ? null : source.External with { },
            Consumer = source.Consumer == null ? null : source.Consumer with { },
            SubjectTransforms = source.SubjectTransforms?.Select(x => x with { }).ToList()
        };
    }

    internal NatsOpts ToNatsOpts()
    {
        return NatsOpts.Default with
        {
            Url = ConnectionString,
            Name = ClientName ?? "wolverine-nats",
            ConnectTimeout = ConnectTimeout,
            CommandTimeout = RequestTimeout,
            AuthOpts = new NatsAuthOpts
            {
                Username = Username,
                Password = Password,
                Token = Token,
                Jwt = Jwt,
                Seed = NKeySeed,
                CredsFile = CredentialsFile,
                NKeyFile = NKeyFile,
                AuthCredCallback = AuthCallback
            },
            TlsOpts = new NatsTlsOpts
            {
                Mode = TlsMode,
                InsecureSkipVerify = TlsInsecure,
                CertFile = ClientCertFile,
                KeyFile = ClientKeyFile,
                CaFile = CaFile
            }
        };
    }

}

/// <summary>
/// Transport-wide defaults for the JetStream consumers and streams Wolverine creates itself.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AckWait"/>, <see cref="MaxDeliver"/> and <see cref="DeliverPolicy"/> apply to the consumers of
/// Wolverine's JetStream listeners, where per-endpoint settings win. (<c>AckPolicy</c> is always
/// <c>Explicit</c> for Wolverine consumers.)
/// </para>
/// <para>
/// The stream limits -- <see cref="MaxAge"/>, <see cref="MaxMessages"/>, <see cref="MaxBytes"/> and
/// <see cref="Replicas"/> -- apply only to a stream that resource setup (<c>resources setup</c> /
/// <c>AddResourceSetupOnStartup()</c>) creates because a JetStream endpoint's stream does not exist yet. A stream
/// declared through <c>DefineStream()</c> and friends is created from its declaration at startup or by resource
/// setup and does not inherit them: a limit its
/// <see cref="StreamConfiguration"/> leaves unset is unlimited, and its replica count is
/// <see cref="StreamConfiguration.Replicas"/>. Of the stream settings here, only <see cref="DuplicateWindow"/>
/// also applies to declared streams.
/// </para>
/// </remarks>
public class JetStreamDefaults
{
    /// <summary>
    /// Maximum age of a stream that resource setup creates for an endpoint. Not applied to declared
    /// streams; see the remarks on <see cref="JetStreamDefaults"/>.
    /// </summary>
    public TimeSpan? MaxAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Maximum message count of a stream that resource setup creates for an endpoint. Not applied to
    /// declared streams; see the remarks on <see cref="JetStreamDefaults"/>.
    /// </summary>
    public long? MaxMessages { get; set; } = 1_000_000;

    /// <summary>
    /// Maximum size of a stream that resource setup creates for an endpoint. Not applied to declared
    /// streams; see the remarks on <see cref="JetStreamDefaults"/>.
    /// </summary>
    public long? MaxBytes { get; set; } = 1024 * 1024 * 1024;

    /// <summary>
    /// Replica count of a stream that resource setup creates for an endpoint. Not applied to declared
    /// streams, which use <see cref="StreamConfiguration.Replicas"/>; see the remarks on <see cref="JetStreamDefaults"/>.
    /// </summary>
    public int Replicas { get; set; } = 1;

    public TimeSpan AckWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default maximum delivery attempts (<c>MaxDeliver</c>) for Wolverine's JetStream consumers: how often
    /// JetStream delivers a message that is not acknowledged. When a failing message goes to the dead letter
    /// subject is decided by Wolverine's error handling, not by this number. A per-endpoint
    /// <c>ConfigureDeadLetterQueue(maxDeliveryAttempts, ...)</c> overrides this.
    /// </summary>
    public int MaxDeliver { get; set; } = 5;

    /// <summary>
    /// Deduplication window applied to auto-provisioned streams. Within this window JetStream
    /// discards messages carrying a duplicate <c>Nats-Msg-Id</c> (see
    /// <see cref="NatsTransportConfiguration.MsgIdSource"/>).
    /// </summary>
    public TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Transport-wide default for the JetStream consumer's <c>DeliverPolicy</c>.
    /// When <c>null</c> (the default) Wolverine leaves the consumer config's
    /// <c>DeliverPolicy</c> unset, which falls through to the NATS server's
    /// own default — <see cref="ConsumerConfigDeliverPolicy.All"/> — replaying
    /// every message currently in the stream when an auto-provisioned consumer
    /// first connects.
    ///
    /// Per-listener overrides via <c>NatsListenerConfiguration.DeliverFrom(...)</c>
    /// always win over this transport-wide default. The override only applies
    /// to consumers Wolverine itself auto-provisions; pre-created consumers
    /// referenced by name keep whatever <c>DeliverPolicy</c> they were
    /// originally created with.
    ///
    /// A stream with work-queue retention only accepts consumers that deliver
    /// all. Shard listeners on the streams <c>UseShardedNatsSubjects()</c>
    /// declares therefore ignore this default; a listener on a work-queue stream
    /// the application declares needs <c>DeliverFrom(All)</c> when it is set.
    /// </summary>
    public ConsumerConfigDeliverPolicy? DeliverPolicy { get; set; }
}

public interface ITenantIdResolver
{
    string? ResolveTenantId(Envelope envelope);
}

public interface ISubjectResolver
{
    string ResolveSubject(string baseSubject, Envelope envelope);
    string? ExtractTenantId(string subject);
}
