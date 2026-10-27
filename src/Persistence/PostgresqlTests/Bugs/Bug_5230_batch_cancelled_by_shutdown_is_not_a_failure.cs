using System.Collections.Concurrent;
using System.Data.Common;
using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace PostgresqlTests.Bugs;

/// <summary>
/// GH-5230: stopping a host while a database operation batch was in flight logged the batcher's own
/// shutdown cancellation as an Error-level <see cref="DatabaseBatchCommandException"/>. A genuine SQL
/// failure must still be reported.
/// </summary>
public class Bug_5230_batch_cancelled_by_shutdown_is_not_a_failure
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_batch_cancelled_by_shutdown_is_not_logged_as_a_database_failure()
    {
        var errors = new BatchErrorCollector();
        using var host = await startHostAsync(errors);

        var operation = new HeldOperation(fail: false);
        await ((IMessageDatabase)host.Services.GetRequiredService<IWolverineRuntime>().Storage)
            .EnqueueAsync(operation);
        await operation.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        await host.StopAsync(Token);

        errors.BatchFailures.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_genuine_sql_failure_is_still_logged()
    {
        var errors = new BatchErrorCollector();
        using var host = await startHostAsync(errors);

        await ((IMessageDatabase)host.Services.GetRequiredService<IWolverineRuntime>().Storage)
            .EnqueueAsync(new HeldOperation(fail: true));
        await errors.Failure.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        await host.StopAsync(Token);

        errors.BatchFailures.ShouldNotBeEmpty();
    }

    private static async Task<IHost> startHostAsync(BatchErrorCollector errors)
    {
        var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(errors))
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug_5230");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        return host;
    }
}

internal sealed class HeldOperation(bool fail) : IDatabaseOperation
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Description => "Hold an active database batch until shutdown cancels it";

    public void ConfigureCommand(Weasel.Core.DbCommandBuilder builder) => builder.Append(fail ? "select 1 / 0;" : "select 1;");

    public async Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands() => [];
}

internal sealed class BatchErrorCollector : ILoggerProvider
{
    private readonly ConcurrentQueue<Exception> _failures = new();

    public TaskCompletionSource Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception[] BatchFailures => _failures.ToArray();

    public ILogger CreateLogger(string categoryName) => new CollectorLogger(this);

    public void Dispose()
    {
    }

    private sealed class CollectorLogger(BatchErrorCollector owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error || exception is not DatabaseBatchCommandException) return;

            owner._failures.Enqueue(exception);
            owner.Failure.TrySetResult();
        }
    }
}
