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
    public string? DefaultQueueGroup { get; set; }
    public bool NormalizeSubjects { get; set; } = true;
    public JetStreamDefaults JetStreamDefaults { get; set; } = new();

    public ITenantIdResolver? TenantIdResolver { get; set; }
    public ISubjectResolver? SubjectResolver { get; set; }
    public string? TenantSubjectPrefix { get; set; }
    public Dictionary<string, StreamConfiguration> Streams { get; set; } = new();

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

    internal NatsJSOpts? ToJetStreamOpts()
    {
        if (!EnableJetStream)
        {
            return null;
        }

        return new NatsJSOpts(ToNatsOpts(), JetStreamDomain, JetStreamApiPrefix ?? "$JS.API");
    }
}

public class JetStreamDefaults
{
    public string Retention { get; set; } = "limits";
    public TimeSpan? MaxAge { get; set; } = TimeSpan.FromDays(7);
    public long? MaxMessages { get; set; } = 1_000_000;
    public long? MaxBytes { get; set; } = 1024 * 1024 * 1024;
    public int Replicas { get; set; } = 1;
    public string AckPolicy { get; set; } = "explicit";
    public TimeSpan AckWait { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxDeliver { get; set; } = 3;
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
