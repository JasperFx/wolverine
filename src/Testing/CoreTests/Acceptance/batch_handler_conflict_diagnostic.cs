using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Xunit;

namespace CoreTests.Acceptance;

public class batch_handler_conflict_diagnostic
{
    private static IHostBuilder buildHost(Action<WolverineOptions> configure, CapturingLogger? logger = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                if (logger != null)
                {
                    opts.Services.AddSingleton<ILoggerFactory>(new SingleLoggerFactory(logger));
                }

                configure(opts);
            });
    }

    [Fact]
    public async Task no_conflict_by_default_because_the_direct_and_the_batch_handler_both_run()
    {
        // GH-5136. Each handler type has its own chain, so the direct handler never shadows the batch handler:
        // the batch moves to a queue of its own and both run. Nothing to warn about. GH-5139 removed the
        // obsolete AssertNoBatchHandlerConflicts() that used to opt in to throwing.
        var logger = new CapturingLogger();

        using var host = await buildHost(opts =>
        {
            opts.Discovery.IncludeType<ConflictDirectHandler>();
            opts.Discovery.IncludeType<ConflictBatchHandler>();
            opts.BatchMessagesOf<ConflictMessage>();
        }, logger).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        logger.Entries.ShouldNotContain(x => x.Message.Contains("Batch handler conflict"));
    }

    [Fact]
    public async Task no_conflict_when_only_a_batch_handler_exists()
    {
        // A BatchMessagesOf<T> with no direct Handle(T) is the normal case - must not warn or throw.
        var logger = new CapturingLogger();

        using var host = await buildHost(opts =>
        {
            opts.Discovery.IncludeType<ConflictBatchHandler>();
            opts.BatchMessagesOf<ConflictMessage>();
        }, logger).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        logger.Entries.ShouldNotContain(x => x.Message.Contains("Batch handler conflict"));
    }
}

public record ConflictMessage(string Name);

public class ConflictDirectHandler
{
    public void Handle(ConflictMessage message)
    {
    }
}

public class ConflictBatchHandler
{
    public void Handle(ConflictMessage[] messages)
    {
    }
}
