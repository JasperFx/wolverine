using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class leadership_lock_cancellation
{
    [Fact]
    public async Task shutdown_cancellation_during_lock_acquisition_is_not_an_error()
    {
        using var lifetime = new CancellationTokenSource();
        var logger = new RecordingLogger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (controller, persistence) = buildController(logger, lifetime.Token);
        persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.TrySetResult();
            // Like the PostgreSQL advisory-lock semaphore, this wait observes the controller's token.
            return await release.Task.WaitAsync(call.Arg<CancellationToken>());
        });

        var check = controller.DoHealthChecksAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await lifetime.CancelAsync();
            await check.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            logger.Errors.ShouldBeEmpty();
        }
        finally
        {
            release.TrySetResult(false);
            await check;
        }
    }

    [Fact]
    public async Task independent_cancellation_while_running_is_still_an_error()
    {
        var logger = new RecordingLogger();
        var (controller, persistence) = buildController(logger, CancellationToken.None);
        var failure = new OperationCanceledException(new CancellationToken(true));
        persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(failure));

        await controller.DoHealthChecksAsync();

        logger.Errors.Single().ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task lock_failure_remains_an_error_even_when_shutdown_has_started()
    {
        using var lifetime = new CancellationTokenSource();
        var logger = new RecordingLogger();
        var (controller, persistence) = buildController(logger, lifetime.Token);
        var failure = new InvalidOperationException("Independent lock failure");
        persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await lifetime.CancelAsync();
            return await Task.FromException<bool>(failure);
        });

        await controller.DoHealthChecksAsync();

        logger.Errors.Single().ShouldBeSameAs(failure);
    }

    private static (NodeAgentController Controller, INodeAgentPersistence Persistence) buildController(
        ILogger logger, CancellationToken cancellation)
    {
        var options = new WolverineOptions();
        options.Durability.Mode = DurabilityMode.Solo;
        options.Durability.DurabilityAgentEnabled = false;
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);
        runtime.Observer.Returns(Substitute.For<IWolverineObserver>());
        var persistence = Substitute.For<INodeAgentPersistence>();
        persistence.MarkHealthCheckAsync(Arg.Any<WolverineNode>(), Arg.Any<CancellationToken>()).Returns(true);
        persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>()).Returns(
            new NodeAgentState([new WolverineNode
            {
                NodeId = options.UniqueNodeId,
                AssignedNodeNumber = options.Durability.AssignedNodeNumber,
                LastHealthCheck = DateTimeOffset.UtcNow,
                ControlUri = new Uri("stub://self")
            }], new AgentRestrictions()));

        return (new NodeAgentController(runtime, persistence, [], logger, cancellation), persistence);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<Exception?> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errors.Add(exception);
        }
    }
}
