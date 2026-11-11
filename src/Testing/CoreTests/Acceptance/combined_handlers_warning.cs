using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// GH-5133. ClassicCombineIntoOneLogicalHandler is obsolete in 6.x and removed in 7.0, so an application
/// still relying on it hears about it at startup -- and can make that a failure in CI. GH-5136: from 7.0 every
/// handler type gets its own chain, so nothing is combined and neither the warning nor the assertion fires.
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
    public async Task nothing_is_combined_so_there_is_no_warning_by_default()
    {
        var logger = new CapturingLogger();

        // GH-5136. No MultipleHandlerBehavior setting: every handler type gets its own chain, so nothing is
        // combined and there is nothing to warn about
        using var host = await buildHost(opts =>
        {
            opts.Discovery.IncludeType<ShippingOrderPlacedHandler>();
            opts.Discovery.IncludeType<BillingOrderPlacedHandler>();
        }, logger).StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.ShouldNotContain(x => x.Message.Contains("ClassicCombineIntoOneLogicalHandler"));

        host.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>()
            .As<Wolverine.Runtime.WolverineRuntime>().Handlers.AllChains()
            .Count(x => x.MessageType == typeof(CombinedOrderPlaced)).ShouldBe(2);
    }

    [Fact]
    public async Task two_methods_on_one_handler_class_for_one_message_is_a_startup_error()
    {
        // GH-5136. Two methods of one class for the same message would be one chain running both -- the
        // combining that 7.0 removed -- so it is refused at startup instead
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await buildHost(opts =>
            {
                opts.Discovery.IncludeType<OnlyOneHandlerForThis>();
            }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain(typeof(OnlyOneHandlerForThis).FullName!);
        ex.Message.ShouldContain(typeof(NotCombinedMessage).FullName!);
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
    public async Task AssertNoCombinedHandlers_is_satisfied_by_default()
    {
        // GH-5136. Nothing is ever combined in 7.0, so the 6.x upgrade rehearsal passes without any setting
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
            }).StartAsync(TestContext.Current.CancellationToken);
        #endregion
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
