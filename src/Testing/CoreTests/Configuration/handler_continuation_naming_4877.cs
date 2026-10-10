using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

// GH-4877. The one middleware frame GH-4714 left numbering from a process-wide static: a Before / Validate
// method returning HandlerContinuation got its local named `result_of_Validate{N}` with N = "how many
// continuation frames this process has built so far". Under CritterWatch's embedded static codegen -- a
// booted host that plans some chains at start-up in an order that varies run to run -- that produced
// suffix-only diffs in ~12 files on every regeneration and failed a drift gate whose suites were green.

public record FirstGated(string Name);

public record SecondGated(string Name);

public record TwiceGated(string Name);

[WolverineIgnore]
public static class FirstGatedHandler
{
    public static HandlerContinuation Validate(FirstGated command)
        => string.IsNullOrEmpty(command.Name) ? HandlerContinuation.Stop : HandlerContinuation.Continue;

    public static void Handle(FirstGated command)
    {
    }
}

[WolverineIgnore]
public static class SecondGatedHandler
{
    public static HandlerContinuation Validate(SecondGated command)
        => string.IsNullOrEmpty(command.Name) ? HandlerContinuation.Stop : HandlerContinuation.Continue;

    public static void Handle(SecondGated command)
    {
    }
}

[WolverineIgnore]
public static class TwiceGatedHandler
{
    public static HandlerContinuation Validate(TwiceGated command)
        => string.IsNullOrEmpty(command.Name) ? HandlerContinuation.Stop : HandlerContinuation.Continue;

    public static HandlerContinuation Before(TwiceGated command)
        => command.Name == "stop" ? HandlerContinuation.Stop : HandlerContinuation.Continue;

    public static void Handle(TwiceGated command)
    {
    }
}

public class handler_continuation_naming_4877
{
    /// <summary>
    /// The property the issue is about, through the real bootstrap: two chains in one host, each with one
    /// continuation-returning Validate, must BOTH be the first in their own chain. Under the static counter
    /// the second chain compiled came out one higher than the first, and which number either got depended
    /// on everything the process had compiled before them.
    /// </summary>
    [Fact]
    public async Task two_chains_in_one_host_each_number_from_zero()
    {
        using var host = await startAsync(typeof(FirstGatedHandler), typeof(SecondGatedHandler));
        var runtime = host.GetRuntime();

        runtime.Handlers.HandlerFor<FirstGated>();
        runtime.Handlers.HandlerFor<SecondGated>();

        var first = runtime.Handlers.ChainFor<FirstGated>()!.SourceCode ?? string.Empty;
        var second = runtime.Handlers.ChainFor<SecondGated>()!.SourceCode ?? string.Empty;

        first.ShouldContain("var result_of_Validate =");
        second.ShouldContain("var result_of_Validate =");

        // Neither carries a suffix: each is the first continuation frame in its own method
        Regex.IsMatch(first, @"result_of_Validate\d").ShouldBeFalse(first);
        Regex.IsMatch(second, @"result_of_Validate\d").ShouldBeFalse(second);
    }

    /// <summary>
    /// The same chain compiled in two different processes-worth of history has to come out byte for byte
    /// the same: here, a host that compiled nothing before it against a host that compiled another gated
    /// chain first.
    /// </summary>
    [Fact]
    public async Task a_chains_source_does_not_depend_on_what_the_host_compiled_before_it()
    {
        string alone;
        using (var host = await startAsync(typeof(SecondGatedHandler)))
        {
            host.GetRuntime().Handlers.HandlerFor<SecondGated>();
            alone = host.GetRuntime().Handlers.ChainFor<SecondGated>()!.SourceCode!;
        }

        string afterAnother;
        using (var host = await startAsync(typeof(FirstGatedHandler), typeof(SecondGatedHandler)))
        {
            var runtime = host.GetRuntime();
            runtime.Handlers.HandlerFor<FirstGated>();
            runtime.Handlers.HandlerFor<SecondGated>();
            afterAnother = runtime.Handlers.ChainFor<SecondGated>()!.SourceCode!;
        }

        afterAnother.ShouldBe(alone);
    }

    /// <summary>
    /// The suffix still has to exist: two continuation frames in ONE generated method would otherwise
    /// collide on the same local. That is what the static counter was for, and the per-chain index keeps it.
    /// </summary>
    [Fact]
    public async Task two_continuations_in_one_chain_are_disambiguated()
    {
        using var host = await startAsync(typeof(TwiceGatedHandler));
        var runtime = host.GetRuntime();

        runtime.Handlers.HandlerFor<TwiceGated>();
        var source = runtime.Handlers.ChainFor<TwiceGated>()!.SourceCode ?? string.Empty;

        // One of the two takes the bare name and the other the first suffix, whichever order the
        // middleware policy placed them in
        var locals = Regex.Matches(source, @"var (result_of_(?:Validate|Before)\d*) =")
            .Select(x => x.Groups[1].Value)
            .ToList();
        locals.Count.ShouldBe(2, source);
        locals.Count(x => Regex.IsMatch(x, @"\d$")).ShouldBe(1, source);
        locals.Count(x => !Regex.IsMatch(x, @"\d$")).ShouldBe(1, source);
    }

    private static Task<IHost> startAsync(params Type[] handlerTypes)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(handler_continuation_naming_4877).Assembly;
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var type in handlerTypes)
                {
                    discovery.IncludeType(type);
                }
            }).StartAsync(TestContext.Current.CancellationToken);
    }
}
