using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.RavenDb.Internals.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace RavenDbTests;

[Collection("raven")]
public class durability_recovery_orphaned_listener : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;
    private CapturingLogger _capturingLogger = null!;
    private IHost _host = null!;

    public durability_recovery_orphaned_listener(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _store = _fixture.StartRavenStore();
        _capturingLogger = new CapturingLogger();

        _host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(_capturingLogger));
            })
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(_store);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ServiceName = "orphaned-listener";
                opts.UseRavenDbPersistence();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task recovery_does_not_throw_for_incoming_messages_targeting_an_unknown_listener_uri()
    {
        var orphanUri = new Uri("tcp://localhost:59999");

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new IncomingMessage
            {
                Id = "IncomingMessages/orphan",
                EnvelopeId = Guid.NewGuid(),
                ReceivedAt = orphanUri,
                OwnerId = 0,
                Status = EnvelopeStatus.Incoming,
                Body = Array.Empty<byte>(),
                MessageType = "orphaned"
            });
            await session.SaveChangesAsync();
        }

        var store = _host.Services.GetRequiredService<IMessageStore>().As<RavenDbMessageStore>();
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var agent = (RavenDbDurabilityAgent)store.BuildAgent(runtime);

        var method = typeof(RavenDbDurabilityAgent).GetMethod(
            "tryRecoverIncomingMessages",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(agent, null)!;

        _capturingLogger.Entries.ShouldNotContain(e => e.Exception is NullReferenceException);
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly CapturingLogger _logger;
        public CapturingLoggerProvider(CapturingLogger logger) => _logger = logger;
        public ILogger CreateLogger(string categoryName) => _logger;
        public void Dispose() { }
    }
}
