using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests.Bugs;

/// <summary>
/// GH-3576. Wolverine and JasperFx each detect a host adopting an application assembly pinned by an earlier
/// host (GH-3521). Marten logs JasperFx's copy from its store activator and Wolverine logs its own, so a
/// Wolverine + Marten host warned twice about the same thing. Wolverine owns the warning in its hosts.
/// </summary>
public class Bug_3576_one_application_assembly_reuse_warning_per_host
{
    [Fact]
    public async Task a_wolverine_and_marten_host_warns_exactly_once()
    {
        var previousJasperFx = JasperFxOptions.RememberedApplicationAssembly;
        var previousWolverine = WolverineOptions.RememberedApplicationAssembly;

        // Act as a later host in a process whose first host registered from another assembly.
        var pinnedElsewhere = typeof(OrderSagaSample.Order).Assembly;
        JasperFxOptions.RememberedApplicationAssembly = pinnedElsewhere;
        WolverineOptions.RememberedApplicationAssembly = pinnedElsewhere;

        var logs = new CapturingLoggerProvider();

        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .ConfigureLogging(x => x.AddProvider(logs))
                .UseWolverine(opts =>
                {
                    opts.Services.AddMarten(o =>
                    {
                        o.Connection(Servers.PostgresConnectionString);
                        o.DatabaseSchemaName = "bug_3576";
                    })
                    // Registers MartenActivator, the hosted service that logs JasperFx's copy of the warning
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Durability.Mode = DurabilityMode.Solo;
                }).StartAsync(TestContext.Current.CancellationToken);

            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            JasperFxOptions.RememberedApplicationAssembly = previousJasperFx;
            WolverineOptions.RememberedApplicationAssembly = previousWolverine;
        }

        var reuseWarnings = logs.Warnings
            .Where(x => x.Contains("adopted application assembly", StringComparison.Ordinal))
            .ToArray();

        reuseWarnings.Length.ShouldBe(1, string.Join(Environment.NewLine, reuseWarnings));
        reuseWarnings[0].ShouldStartWith("Wolverine adopted application assembly 'OrderSagaSample'");
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider parent) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    parent.Warnings.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
