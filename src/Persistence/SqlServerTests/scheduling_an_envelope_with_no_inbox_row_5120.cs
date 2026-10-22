using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace SqlServerTests;

/// <summary>
/// GH-5120. Rescheduling is an UPDATE of an existing inbox row. One that matched nothing used to succeed
/// silently, which is how a durable batch's retry vanished without a trace. It now says so.
/// </summary>
public class scheduling_an_envelope_with_no_inbox_row_5120 : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> theWarnings = new();
    private IHost theHost = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(new WarningCapturingLoggerProvider(theWarnings)))
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "gh_5120_schedule");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync(Token);

        await theHost.ResetResourceState(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static Envelope aScheduledEnvelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        return envelope;
    }

    [Fact]
    public async Task scheduling_an_envelope_that_was_never_persisted_warns()
    {
        var envelope = aScheduledEnvelope();

        await theHost.GetRuntime().Storage.Inbox.ScheduleExecutionAsync(envelope);

        theWarnings.ShouldContain(x => x.Contains(envelope.Id.ToString()) && x.Contains("matched no inbox row"));
    }

    [Fact]
    public async Task scheduling_a_persisted_envelope_does_not_warn()
    {
        var envelope = aScheduledEnvelope();
        var inbox = theHost.GetRuntime().Storage.Inbox;
        await inbox.StoreIncomingAsync(envelope);

        await inbox.ScheduleExecutionAsync(envelope);

        theWarnings.ShouldNotContain(x => x.Contains("matched no inbox row"));
    }

    private class WarningCapturingLoggerProvider(ConcurrentQueue<string> warnings) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new WarningLogger(warnings);

        public void Dispose()
        {
        }

        private class WarningLogger(ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}
