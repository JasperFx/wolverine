using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
///     GH-3973, following up GH-3867. A batched element type that also has unbatched handlers has two
///     independent execution paths writing the same entity unless a partitioned topology sequences them.
///     Without one, the failure is intermittent concurrency collisions under load — the silent version is
///     the bug, so startup says it out loud.
/// </summary>
public class unsequenced_batch_execution_validation
{
    [Fact]
    public async Task throws_when_the_batch_cannot_be_sequenced_against_its_unbatched_siblings()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(UnsequencedProbeHandler))
                        .IncludeType(typeof(UnsequencedProbeBatchHandler));

                    // Both handlers legitimately run under Separated -- which is exactly when there are
                    // two writers
                    opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                    opts.BatchMessagesOf<UnsequencedProbe>();

                    opts.AssertBatchExecutionIsSequenced();
                }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain("Unsequenced batch execution");
        ex.Message.ShouldContain(nameof(UnsequencedProbe));

        // The message has to name the fix, not just the problem
        ex.Message.ShouldContain("GlobalPartitioned");

        // And it has to head off the obvious wrong fix
        ex.Message.ShouldContain("Sequential() on the batch queue does NOT close this");
    }

    /// <summary>
    ///     A partitioned topology is what GH-3867 uses to point the batch at the same slots as its
    ///     unbatched siblings, so every writer for one group id really is a single writer.
    /// </summary>
    [Fact]
    public async Task does_not_complain_when_a_partitioned_topology_sequences_them()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PartitionedProbeHandler))
                    .IncludeType(typeof(PartitionedProbeBatchHandler));

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                // Same harness GH-3867's own resolution test uses -- this is what gives the batch
                // execution slots to share with its unbatched siblings
                opts.MessagePartitioning.PublishToPartitionedLocalMessaging("gh3973", 4,
                    topology => { topology.MessagesImplementing<IPartitionedProbeMessage>(); });

                opts.BatchMessagesOf<PartitionedProbe>();

                // Would throw if the batch were left unsequenced
                opts.AssertBatchExecutionIsSequenced();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.ShouldNotBeNull();
    }

    /// <summary>
    ///     No unbatched sibling means no second writer, so there is nothing to warn about.
    /// </summary>
    [Fact]
    public async Task silent_when_the_element_type_has_no_unbatched_handler()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(BatchOnlyProbeBatchHandler));

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.BatchMessagesOf<BatchOnlyProbe>();

                opts.AssertBatchExecutionIsSequenced();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.ShouldNotBeNull();
    }

    /// <summary>
    ///     Under the default Classic behaviour the direct handler wins and the batch handler never runs at
    ///     all, so there is only ever one writer. That shape is already reported by
    ///     <c>warnOrAssertBatchHandlerConflicts</c> and must not be double-reported here.
    /// </summary>
    [Fact]
    public async Task silent_under_classic_behavior_where_the_batch_never_runs()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ClassicProbeHandler))
                    .IncludeType(typeof(ClassicProbeBatchHandler));

                // The default, stated explicitly for the sake of the test
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.ClassicCombineIntoOneLogicalHandler;
                opts.BatchMessagesOf<ClassicProbe>();

                opts.AssertBatchExecutionIsSequenced();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.ShouldNotBeNull();
    }

    [Fact]
    public async Task warns_rather_than_throwing_by_default()
    {
        var logger = new RecordingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logger))
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(UnsequencedProbeHandler))
                    .IncludeType(typeof(UnsequencedProbeBatchHandler));

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.BatchMessagesOf<UnsequencedProbe>();

                // Deliberately NOT calling AssertBatchExecutionIsSequenced()
            }).StartAsync(TestContext.Current.CancellationToken);

        logger.Warnings.ShouldContain(x => x.Contains("Unsequenced batch execution"));
    }
}

public record UnsequencedProbe(string GroupId);

[WolverineIgnore]
public static class UnsequencedProbeHandler
{
    public static void Handle(UnsequencedProbe message)
    {
    }
}

[WolverineIgnore]
public static class UnsequencedProbeBatchHandler
{
    public static void Handle(UnsequencedProbe[] batch)
    {
    }
}

public interface IPartitionedProbeMessage;

public record PartitionedProbe(string GroupId) : IPartitionedProbeMessage;

[WolverineIgnore]
public static class PartitionedProbeHandler
{
    public static void Handle(PartitionedProbe message)
    {
    }
}

[WolverineIgnore]
public static class PartitionedProbeBatchHandler
{
    public static void Handle(PartitionedProbe[] batch)
    {
    }
}

public record BatchOnlyProbe(string GroupId);

[WolverineIgnore]
public static class BatchOnlyProbeBatchHandler
{
    public static void Handle(BatchOnlyProbe[] batch)
    {
    }
}

public record ClassicProbe(string GroupId);

[WolverineIgnore]
public static class ClassicProbeHandler
{
    public static void Handle(ClassicProbe message)
    {
    }
}

[WolverineIgnore]
public static class ClassicProbeBatchHandler
{
    public static void Handle(ClassicProbe[] batch)
    {
    }
}

public class RecordingLoggerProvider : ILoggerProvider
{
    public List<string> Warnings { get; } = [];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(Warnings);

    public void Dispose()
    {
    }

    private class RecordingLogger(List<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;

            lock (warnings)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }
}
