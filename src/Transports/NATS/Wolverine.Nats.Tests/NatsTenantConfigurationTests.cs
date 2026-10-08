using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using Shouldly;
using Testcontainers.Nats;
using Wolverine.Nats.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// <c>AddTenant(id, cfg => ...)</c> used to copy the transport's configuration when it was called, so transport
/// settings made afterwards never reached the tenant, and only the connection part of the copy was ever read: a
/// tenant's streams, provisioning settings and JetStream domain were silently ignored. The action now runs when
/// the transport connects, against a copy of the configuration as it is then, and its stream and JetStream settings
/// apply to the tenant's own connection.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsTenantConfigurationTests : IClassFixture<SecondNatsServer>
{
    private readonly NatsContainerFixture _fixture;
    private readonly SecondNatsServer _tenantServer;
    private readonly ITestOutputHelper _output;

    public NatsTenantConfigurationTests(NatsContainerFixture fixture, SecondNatsServer tenantServer,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _tenantServer = tenantServer;
        _output = output;
    }

    [Fact]
    public async Task transport_settings_made_after_add_tenant_reach_the_tenant()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "TenantLateSettings";
                opts.UseNats(_fixture.ConnectionString)
                    .ConfigureNatsOpts(o => o with { SubPendingChannelCapacity = 1111 })
                    .AddTenant("t1", cfg => cfg.ConnectionString = _fixture.ConnectionString)
                    .ConfigureNatsOpts(o => o with { SubPendingChannelCapacity = 2222 })
                    .ConfigureTimeouts(5.Seconds(), 7.Seconds());
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var tenant = host.GetRuntime().Options.Transports.GetOrCreate<NatsTransport>().Tenants["t1"].Connection
            .ShouldNotBeNull().Opts;

        tenant.SubPendingChannelCapacity.ShouldBe(2222);
        tenant.ConnectTimeout.ShouldBe(5.Seconds());
        tenant.CommandTimeout.ShouldBe(7.Seconds());
    }

    [Fact]
    public async Task a_tenant_declares_its_own_streams_and_stream_defaults()
    {
        var names = new Names();

        using var host = await startAsync(names, cfg =>
        {
            cfg.JetStreamDefaults.DuplicateWindow = 10.Seconds();
            cfg.Streams[names.TenantStream] = new StreamConfiguration
            {
                Name = names.TenantStream,
                Subjects = [$"{names.TenantRoot}.>"]
            };
        });

        var js = await _tenantServer.JetStreamAsync();
        (await js.GetStreamAsync(names.Stream, cancellationToken: TestContext.Current.CancellationToken))
            .Info.Config.DuplicateWindow.ShouldBe(10.Seconds());
        (await js.GetStreamAsync(names.TenantStream, cancellationToken: TestContext.Current.CancellationToken))
            .Info.Config.Name.ShouldBe(names.TenantStream);

        // The transport's own server only gets the transport's streams
        await using var shared = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await Should.ThrowAsync<NatsJSApiException>(() => shared.CreateJetStreamContext()
            .GetStreamAsync(names.TenantStream, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task a_tenant_with_auto_provision_off_gets_no_streams()
    {
        var names = new Names();

        using var host = await startAsync(names, cfg => cfg.AutoProvision = false);

        var js = await _tenantServer.JetStreamAsync();
        await Should.ThrowAsync<NatsJSApiException>(() =>
            js.GetStreamAsync(names.Stream, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task a_tenant_under_verify_fails_the_start_for_its_missing_stream()
    {
        var names = new Names();

        var exception = await Should.ThrowAsync<Exception>(() =>
            startAsync(names, cfg => cfg.Provisioning = NatsProvisioning.Verify));

        var message = flatten(exception);
        _output.WriteLine(message);
        message.ShouldContain($"stream '{names.Stream}' does not exist (tenant 't1')");

        var js = await _tenantServer.JetStreamAsync();
        await Should.ThrowAsync<NatsJSApiException>(() =>
            js.GetStreamAsync(names.Stream, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task a_tenant_uses_its_own_jetstream_domain()
    {
        var names = new Names();

        // No JetStream answers for this domain on the tenant's server, so provisioning there has to fail
        var exception = await Should.ThrowAsync<Exception>(() => startAsync(names, cfg =>
        {
            cfg.JetStreamDomain = "no-such-domain";
            cfg.RequestTimeout = 2.Seconds();
        }));
        _output.WriteLine(flatten(exception));

        var js = await _tenantServer.JetStreamAsync();
        await Should.ThrowAsync<NatsJSApiException>(() =>
            js.GetStreamAsync(names.Stream, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task a_tenant_changing_a_transport_wide_setting_is_warned_about()
    {
        var names = new Names();
        var logs = new WarningRecorder();

        using var host = await startAsync(names, cfg =>
        {
            cfg.DefaultQueueGroup = "tenant-workers";
            cfg.NormalizeSubjects = false;
            cfg.ConfigureJetStreamPublish = (_, pubOpts) => pubOpts;
        }, logs);

        logs.Warnings.ShouldContain(x =>
            x.Contains("'t1'") && x.Contains(nameof(NatsTransportConfiguration.DefaultQueueGroup)) &&
            x.Contains(nameof(NatsTransportConfiguration.NormalizeSubjects)) &&
            x.Contains(nameof(NatsTransportConfiguration.ConfigureJetStreamPublish)));
    }

    private Task<IHost> startAsync(Names names, Action<NatsTransportConfiguration> tenant,
        WarningRecorder? logs = null)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l =>
            {
                l.AddXunitLogging(_output);
                if (logs != null)
                {
                    l.AddProvider(logs);
                }
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "TenantStreams";
                opts.BrokerInitializationTimeout = TimeSpan.Zero;
                opts.UseNats(_fixture.ConnectionString)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("t1", cfg =>
                    {
                        cfg.ConnectionString = _tenantServer.ConnectionString;
                        tenant(cfg);
                    })
                    .DefineStream(names.Stream, s => s.WithSubjects($"{names.Root}.>"));
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private static string flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        return string.Join(Environment.NewLine, messages);
    }

    private sealed class Names
    {
        private readonly string _id = Guid.NewGuid().ToString("N")[..8];
        public string Stream => $"TENANTCFG_{_id}";
        public string TenantStream => $"TENANTONLY_{_id}";
        public string Root => $"tenantcfg.{_id}";
        public string TenantRoot => $"tenantonly.{_id}";
    }

    private sealed class WarningRecorder : ILoggerProvider, ILogger
    {
        private readonly List<string> _warnings = new();

        public string[] Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return _warnings.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;

            lock (_warnings)
            {
                _warnings.Add(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A second, independent NATS server for a tenant's dedicated connection, so what lands on it is provably the
/// tenant connection's doing
/// </summary>
public sealed class SecondNatsServer : IAsyncLifetime
{
    private NatsContainer? _container;
    private NatsConnection? _connection;

    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _container = new NatsBuilder().WithImage(NatsContainerFixture.NatsImage).Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task<INatsJSContext> JetStreamAsync()
    {
        if (_connection == null)
        {
            _connection = new NatsConnection(new NatsOpts { Url = ConnectionString });
            await _connection.ConnectAsync();
        }

        return _connection.CreateJetStreamContext();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }

        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}
