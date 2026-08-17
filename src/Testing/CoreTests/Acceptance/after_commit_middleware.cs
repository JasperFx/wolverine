using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
///     GH-3975. <c>After</c> methods are inserted at the FRONT of the postprocessors, and the transactional
///     commit is itself a postprocessor, so <c>After</c> runs <b>before</b> the write is durable. These pin the
///     new <c>AfterCommit</c> convention and <c>[WolverineAfterCommit]</c> attribute, which append to a separate
///     list concatenated after every postprocessor.
/// </summary>
/// <remarks>
///     The ordering guarantee is deliberately structural rather than positional — see
///     <see cref="Wolverine.Configuration.IChain.PostCommitPostprocessors" />. These tests assert the chain
///     composition and the observed call order; the per-provider tests assert that the emitted call really does
///     land after each persistence provider's own commit frame.
/// </remarks>
public class after_commit_middleware
{
    private static async Task<IHost> hostFor(Type handlerType)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task after_commit_lands_in_its_own_list_not_in_postprocessors()
    {
        using var host = await hostFor(typeof(AfterCommitOrderHandler));
        var chain = host.Services.GetRequiredService<HandlerGraph>()
            .HandlerFor<AfterCommitOrderMessage>()!.As<MessageHandler>().Chain!;

        chain.Postprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(AfterCommitOrderHandler.After));

        // The load-bearing distinction: NOT in Postprocessors, where a persistence provider's commit frame
        // could end up after it.
        chain.Postprocessors.OfType<MethodCall>()
            .ShouldNotContain(x => x.Method.Name == nameof(AfterCommitOrderHandler.AfterCommit));

        chain.PostCommitPostprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(AfterCommitOrderHandler.AfterCommit));
    }

    [Fact]
    public async Task the_conventional_method_name_and_the_attribute_both_work()
    {
        using var host = await hostFor(typeof(AfterCommitAttributeHandler));
        var chain = host.Services.GetRequiredService<HandlerGraph>()
            .HandlerFor<AfterCommitAttributeMessage>()!.As<MessageHandler>().Chain!;

        chain.PostCommitPostprocessors.OfType<MethodCall>()
            .Select(x => x.Method.Name)
            .ShouldBe([nameof(AfterCommitAttributeHandler.RunMeLast)]);
    }

    [Fact]
    public async Task runs_in_order_after_the_handler_and_after_the_after_method()
    {
        AfterCommitOrderHandler.Calls.Clear();

        using var host = await hostFor(typeof(AfterCommitOrderHandler));
        await host.InvokeMessageAndWaitAsync(new AfterCommitOrderMessage());

        AfterCommitOrderHandler.Calls.ShouldBe(["Handle", "After", "AfterCommit"]);
    }

    /// <summary>
    ///     The reason to want "after the commit" is almost always that the side effect must not happen for a
    ///     write that did not land. Frames are concatenated without a try/finally, so a postprocessor that
    ///     throws unwinds straight past the after-commit frames — this pins that so it stays true.
    /// </summary>
    /// <remarks>
    ///     The handler's own throwing <c>After</c> method stands in for the persistence provider's commit
    ///     frame here. Both are plain postprocessor frames sitting ahead of
    ///     <see cref="Wolverine.Configuration.IChain.PostCommitPostprocessors" />, so they exercise the same
    ///     unwinding path; the per-provider tests cover the real commit frame.
    /// </remarks>
    [Fact]
    public async Task does_not_run_when_an_earlier_postprocessor_throws()
    {
        ThrowingCommitHandler.AfterCommitRan = false;

        using var host = await hostFor(typeof(ThrowingCommitHandler));

        try
        {
            await host.InvokeMessageAndWaitAsync(new ThrowingCommitMessage());
        }
        catch (Exception)
        {
            // expected -- the stand-in commit threw
        }

        ThrowingCommitHandler.AfterCommitRan.ShouldBeFalse(
            "an after-commit method must not run when the commit threw");
    }
}

public record AfterCommitOrderMessage;

[WolverineIgnore]
public static class AfterCommitOrderHandler
{
    public static readonly List<string> Calls = [];

    public static void Handle(AfterCommitOrderMessage message) => Calls.Add("Handle");

    public static void After() => Calls.Add("After");

    public static void AfterCommit() => Calls.Add("AfterCommit");
}

public record AfterCommitAttributeMessage;

[WolverineIgnore]
public static class AfterCommitAttributeHandler
{
    public static void Handle(AfterCommitAttributeMessage message)
    {
    }

    // Deliberately NOT named AfterCommit -- proves the attribute alone is enough
    [WolverineAfterCommit]
    public static void RunMeLast()
    {
    }
}

public record ThrowingCommitMessage;

[WolverineIgnore]
public static class ThrowingCommitHandler
{
    public static bool AfterCommitRan;

    public static void Handle(ThrowingCommitMessage message)
    {
    }

    // Stands in for a persistence provider's commit frame -- a postprocessor ahead of the after-commit list
    public static void After() => throw new InvalidOperationException("the commit failed");

    public static void AfterCommit() => AfterCommitRan = true;
}
