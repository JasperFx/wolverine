using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Xunit;

namespace PostgresqlTests;

/// <summary>
/// GH-5723. A host disposed without being stopped first (<c>using var host = ...</c>) runs no hosted service's
/// <c>StopAsync</c>, and the container disposes singletons in the reverse of the order it created them. The message
/// store's data source is created during Wolverine's startup, after the runtime, so it used to be disposed before
/// Wolverine stopped, and the durability agent logged <see cref="ObjectDisposedException" /> errors on the way down.
/// Wolverine now stops before the services its shutdown needs are disposed
/// </summary>
public class disposing_a_host_without_stopping_it_5723
{
    [Fact]
    public async Task wolverine_stops_before_its_data_source_is_disposed()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync("dispose_5723", TestContext.Current.CancellationToken);
        }

        var errors = new DisposedObjectErrors();
        var handled = new HandledSignal5723();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // The durability agent polls the store all the time, so it is busy when the host goes away
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dispose_5723");
                opts.Services.AddSingleton(handled);
                opts.Services.AddSingleton<ILoggerProvider>(errors);

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DisposeMessage5723Handler));
                opts.Publish(x => x.Message<DisposeMessage5723>().ToLocalQueue("dispose5723").UseDurableInbox());
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.MessageBus().SendAsync(new DisposeMessage5723());
        await handled.Source.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        // Disposed, not stopped
        host.Dispose();

        errors.Messages.ShouldBeEmpty();
    }

    private sealed class DisposedObjectErrors : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is ObjectDisposedException) _messages.Enqueue($"{formatter(state, exception)}: {exception.Message}");
        }
    }
}

public class HandledSignal5723
{
    // Continuations run asynchronously: with a synchronous continuation, the test's host.Dispose() would run on the
    // handler's own thread, and the shutdown would wait on the very handler that is running it
    public readonly TaskCompletionSource Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public record DisposeMessage5723;

[WolverineIgnore]
public static class DisposeMessage5723Handler
{
    public static void Handle(DisposeMessage5723 message, HandledSignal5723 handled) => handled.Source.TrySetResult();
}
