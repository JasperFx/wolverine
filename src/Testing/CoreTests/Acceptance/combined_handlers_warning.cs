using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// GH-5133. ClassicCombineIntoOneLogicalHandler is obsolete in 6.x and removed in 7.0, so an application
/// still relying on it hears about it at startup -- and can make that a failure in CI.
/// </summary>
public class combined_handlers_warning
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
    public async Task warns_once_listing_the_combined_message_types_in_classic_mode()
    {
        var logger = new CapturingLogger();

        // The default MultipleHandlerBehavior is still ClassicCombineIntoOneLogicalHandler
        using var host = await buildHost(opts =>
        {
            opts.Discovery.IncludeType<ShippingOrderPlacedHandler>();
            opts.Discovery.IncludeType<BillingOrderPlacedHandler>();
            opts.Discovery.IncludeType<OnlyOneHandlerForThis>();
        }, logger).StartAsync(TestContext.Current.CancellationToken);

        var warning = logger.Entries
            .Where(x => x.Level == LogLevel.Warning && x.Message.Contains("ClassicCombineIntoOneLogicalHandler"))
            .ShouldHaveSingleItem();

        warning.Message.ShouldContain(typeof(CombinedOrderPlaced).FullName!);
        warning.Message.ShouldContain(typeof(ShippingOrderPlacedHandler).FullName!);
        warning.Message.ShouldContain(typeof(BillingOrderPlacedHandler).FullName!);
        warning.Message.ShouldContain("AssertNoCombinedHandlers()");

        // A message with a single handler class is not combined
        warning.Message.ShouldNotContain(nameof(NotCombinedMessage));
    }

    [Fact]
    public async Task no_warning_when_no_message_type_has_more_than_one_handler_class()
    {
        var logger = new CapturingLogger();

        using var host = await buildHost(opts =>
        {
            // Two methods on ONE class for the same message is still one handler type
            opts.Discovery.IncludeType<OnlyOneHandlerForThis>();
        }, logger).StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.ShouldNotContain(x => x.Message.Contains("ClassicCombineIntoOneLogicalHandler"));
    }

    [Fact]
    public async Task no_warning_under_separated()
    {
        var logger = new CapturingLogger();

        using var host = await buildHost(opts =>
        {
            opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            opts.Discovery.IncludeType<ShippingOrderPlacedHandler>();
            opts.Discovery.IncludeType<BillingOrderPlacedHandler>();
        }, logger).StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.ShouldNotContain(x => x.Message.Contains("ClassicCombineIntoOneLogicalHandler"));
    }

    [Fact]
    public async Task AssertNoCombinedHandlers_fails_startup_on_combined_handlers()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            #region sample_assert_no_combined_handlers
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Discovery.IncludeType<ShippingOrderPlacedHandler>();
                    opts.Discovery.IncludeType<BillingOrderPlacedHandler>();

                    // Rehearse the Wolverine 7.0 upgrade: fail startup if any message
                    // type still has handlers from more than one class combined
                    opts.AssertNoCombinedHandlers();
                }).StartAsync();
            #endregion
        });

        ex.Message.ShouldContain(typeof(CombinedOrderPlaced).FullName!);
    }

    [Fact]
    public async Task AssertNoCombinedHandlers_is_satisfied_by_separated()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<ShippingOrderPlacedHandler>();
                opts.Discovery.IncludeType<BillingOrderPlacedHandler>();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.AssertNoCombinedHandlers();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

}

public record CombinedOrderPlaced(Guid Id);

public record NotCombinedMessage(Guid Id);

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[Wolverine.Attributes.WolverineIgnore]
public class ShippingOrderPlacedHandler
{
    public void Handle(CombinedOrderPlaced message)
    {
    }
}

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[Wolverine.Attributes.WolverineIgnore]
public class BillingOrderPlacedHandler
{
    public void Handle(CombinedOrderPlaced message)
    {
    }
}

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[Wolverine.Attributes.WolverineIgnore]
public class OnlyOneHandlerForThis
{
    public void Handle(NotCombinedMessage message)
    {
    }

    public void Consume(NotCombinedMessage message)
    {
    }
}
