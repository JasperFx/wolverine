using System.Collections.Concurrent;
using System.Data.Common;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Xunit;

namespace PersistenceTests.Durability;

// GH-5247. DurabilityAgent.StopAsync() returned while the expired-handled-envelope cleanup was still
// running, so the command resumed against a data source the host had already disposed. The real
// five-second cleanup timer dispatches the command in every test here, and the data source holds it
// at OpenConnectionAsync() for as long as each test wants.
public class durability_agent_stops_in_flight_commands_5247
{
    private readonly Logs _logs = new();

    [Fact]
    public async Task stopping_the_agent_cancels_the_in_flight_command()
    {
        using var host = await startHostAsync();
        var dataSource = new GatedDataSource(honorCancellation: true);
        var agent = new DurabilityAgent(host.Services.GetRequiredService<IWolverineRuntime>(), databaseFor(dataSource));

        await agent.StartAsync(TestContext.Current.CancellationToken);
        await dataSource.Entered.Task.WaitAsync(15.Seconds(), TestContext.Current.CancellationToken);

        // Never released: only the agent's own cancellation gets the command out
        await agent.StopAsync(TestContext.Current.CancellationToken).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        dataSource.Finished.Task.IsCompleted.ShouldBeTrue();
        dataSource.WasCancelled.ShouldBeTrue();
        _logs.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task stopping_the_agent_waits_for_a_command_that_is_slow_to_cancel()
    {
        using var host = await startHostAsync();
        var dataSource = new GatedDataSource(honorCancellation: false);
        var agent = new DurabilityAgent(host.Services.GetRequiredService<IWolverineRuntime>(), databaseFor(dataSource));

        await agent.StartAsync(TestContext.Current.CancellationToken);
        await dataSource.Entered.Task.WaitAsync(15.Seconds(), TestContext.Current.CancellationToken);

        var stopping = agent.StopAsync(TestContext.Current.CancellationToken);

        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        stopping.IsCompleted.ShouldBeFalse();

        dataSource.Release();
        await stopping.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        dataSource.Finished.Task.IsCompleted.ShouldBeTrue();
        _logs.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task host_shutdown_does_not_leave_a_command_running_against_the_data_source()
    {
        var dataSource = new GatedDataSource(honorCancellation: true);

        using var host = await startHostAsync(services =>
        {
            services.AddSingleton(dataSource);
            services.AddSingleton<IHostedService, DurabilityAgentService>();
        });

        await dataSource.Entered.Task.WaitAsync(15.Seconds(), TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        dataSource.Finished.Task.IsCompleted.ShouldBeTrue();
        _logs.Errors.ShouldBeEmpty();

        // And nothing opens a connection once the host has stopped
        var opens = dataSource.Opens;
        dataSource.Release();
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        dataSource.Opens.ShouldBe(opens);
    }

    private Task<IHost> startHostAsync(Action<IServiceCollection>? configure = null)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(_logs))
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;

                // Only the handled-envelope cleanup timer should reach the data source
                opts.Durability.DurabilityMetricsEnabled = false;
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
            })
            .ConfigureServices(services => configure?.Invoke(services))
            .StartAsync(TestContext.Current.CancellationToken);
    }

    internal static IMessageDatabase databaseFor(DbDataSource dataSource)
    {
        var database = Substitute.For<IMessageDatabase>();
        database.Uri.Returns(new Uri("wolverinedb://postgresql/localhost/gh5247/public"));
        database.Name.Returns("gh5247");
        database.Settings.Returns(new DatabaseSettings { Role = MessageStoreRole.Ancillary });
        database.DataSource.Returns(dataSource);
        database.BatchedDeleteExpiredHandledEnvelopesSql(Arg.Any<int>()).Returns("select 1");

        return database;
    }

    public class DurabilityAgentService : IHostedService
    {
        private readonly IWolverineRuntime _runtime;
        private readonly GatedDataSource _dataSource;
        private DurabilityAgent? _agent;

        public DurabilityAgentService(IWolverineRuntime runtime, GatedDataSource dataSource)
        {
            _runtime = runtime;
            _dataSource = dataSource;
        }

        // Built here rather than in the constructor: hosted services are resolved before Wolverine
        // has started, and the agent needs the runtime's compiled handlers
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _agent = new DurabilityAgent(_runtime, databaseFor(_dataSource));
            return _agent.StartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => _agent!.StopAsync(cancellationToken);
    }

    public class GatedDataSource : DbDataSource
    {
        private readonly bool _honorCancellation;
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _opens;

        public GatedDataSource(bool honorCancellation)
        {
            _honorCancellation = honorCancellation;
        }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }

        public int Opens => Volatile.Read(ref _opens);

        public void Release() => _resume.TrySetResult();

        public override string ConnectionString => "Host=nowhere";

        protected override DbConnection CreateDbConnection() => throw new NotSupportedException();

        protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _opens);
            Entered.TrySetResult();

            try
            {
                if (_honorCancellation)
                {
                    await _resume.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await _resume.Task;
                }

                // A pool that notices the cancellation late, once the open finally gets anywhere
                cancellationToken.ThrowIfCancellationRequested();

                throw new ObjectDisposedException("Npgsql.PoolingDataSource");
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }

    public class Logs : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public IReadOnlyList<string> Errors => _errors.ToArray();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                _errors.Enqueue($"{formatter(state, exception)} {exception}");
            }
        }

        public void Dispose()
        {
        }
    }
}
