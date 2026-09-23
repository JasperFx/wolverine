using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Oracle;
using Wolverine.RDBMS.Transport;

namespace OracleTests.Transport;

/// <summary>
/// Regression guard for the external table poller swallowing its own failures. The Oracle
/// implementation used to catch every exception and write ex.Message to Console.Error, which
/// pre-empted the ILogger call in ExternalMessageTableListener -- a poll failure left nothing at all
/// in the configured log sink. Pointing a listener at a table that does not exist, with Wolverine
/// forbidden from creating it, forces the failure.
/// </summary>
public class external_table_poll_failures_are_logged
{
    [Fact]
    public async Task a_failing_poll_is_reported_through_the_logger()
    {
        var token = TestContext.Current.CancellationToken;
        var logs = new CapturingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "wolverine");

                opts.ListenForMessagesFromExternalDatabaseTable("ext", "table_that_does_not_exist", t =>
                {
                    // Wolverine must not create it -- the missing table is the point
                    t.AllowWolverineControl = false;
                    t.MessageType = typeof(Message1);
                    t.PollingInterval = 250.Milliseconds();
                });
            }).StartAsync(token);

        // The listener sleeps up to 2s before its first poll to de-contend the advisory lock
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline && !logs.HasErrorMentioning("table_that_does_not_exist"))
        {
            await Task.Delay(250.Milliseconds(), token);
        }

        logs.HasErrorMentioning("table_that_does_not_exist")
            .ShouldBeTrue("The failing poll should have been logged as an error by ExternalMessageTableListener");

        await host.StopAsync(token);
    }
}

public class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public bool HasErrorMentioning(string fragment) =>
        Entries.Any(x => x.Level >= LogLevel.Error && x.Message.Contains(fragment));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private class CapturingLogger(CapturingLoggerProvider parent) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            parent.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
