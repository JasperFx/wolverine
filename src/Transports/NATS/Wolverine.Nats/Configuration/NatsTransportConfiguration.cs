using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Wolverine.Nats.Configuration;

public class NatsTransportConfiguration
{
    public string ConnectionString { get; set; } = "nats://localhost:4222";
    public string? ClientName { get; set; }
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Token { get; set; }
    public string? Jwt { get; set; }
    public string? NKeySeed { get; set; }
    public string? CredentialsFile { get; set; }
    public string? NKeyFile { get; set; }
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
