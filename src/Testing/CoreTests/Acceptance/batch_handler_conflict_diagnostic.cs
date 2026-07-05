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
    public async Task warns_by_default_when_a_direct_handler_shadows_a_batch_in_classic_mode()
    {
        var logger = new CapturingLogger();

        using var host = await buildHost(opts =>
        {
            // default MultipleHandlerBehavior is ClassicCombineIntoOneLogicalHandler
            opts.Discovery.IncludeType<ConflictDirectHandler>();
            opts.Discovery.IncludeType<ConflictBatchHandler>();
            opts.BatchMessagesOf<ConflictMessage>();
        }, logger).StartAsync();

        logger.Entries.ShouldContain(x =>
            x.Level == LogLevel.Warning && x.Message.Contains("Batch handler conflict"));

        var warning = logger.Entries
            .Single(x => x.Level == LogLevel.Warning && x.Message.Contains("Batch handler conflict"));

        // Names both roles and points at the fix
        warning.Message.ShouldContain(nameof(ConflictDirectHandler));
        warning.Message.ShouldContain("MultipleHandlerBehavior.Separated");
    }

    [Fact]
    public async Task throws_when_opted_in_via_AssertNoBatchHandlerConflicts()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await buildHost(opts =>
            {
                opts.Discovery.IncludeType<ConflictDirectHandler>();
                opts.Discovery.IncludeType<ConflictBatchHandler>();
                opts.BatchMessagesOf<ConflictMessage>();
                opts.AssertNoBatchHandlerConflicts();
            }).StartAsync();
        });

        ex.Message.ShouldContain("Batch handler conflict");
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
            opts.AssertNoBatchHandlerConflicts();
        }, logger).StartAsync();

        logger.Entries.ShouldNotContain(x => x.Message.Contains("Batch handler conflict"));
    }

    [Fact]
    public async Task no_conflict_under_separated_mode_even_with_both_handlers()
    {
        // Separated legitimately runs both handlers (the batch is moved to its own queue), so the
        // conflict diagnostic must stay silent even when opted in to throwing.
        var logger = new CapturingLogger();

        using var host = await buildHost(opts =>
        {
            opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            opts.Discovery.IncludeType<ConflictDirectHandler>();
            opts.Discovery.IncludeType<ConflictBatchHandler>();
            opts.BatchMessagesOf<ConflictMessage>();
            opts.AssertNoBatchHandlerConflicts();
        }, logger).StartAsync();

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
