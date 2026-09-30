using System.Text.RegularExpressions;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Configuration;
using Wolverine.Middleware;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Configuration;

// GH-4714. Three middleware frames (five, counting the HTTP twins) named their generated variables from a
// process-wide `static int _count`, so a variable's name in one chain depended on how many frames of that
// kind had been constructed EARLIER IN THE PROCESS, in other chains.
//
// Adding a single DI singleton renumbered ~48 unrelated generated handlers across three store flavours, with
// diffs that were purely handlerContinuation1 -> handlerContinuation3. That is a steady source of merge
// conflicts in committed Internal/Generated code, and it makes drift gates fire on files the author never
// touched. The static was also not atomic, so chains compiled concurrently could race on it.
//
// RequirementResultHttpFrame was worse than noisy: it read the static AGAIN at generation time rather than
// the value captured at construction, so two of them in one method emitted the same `problemDetails{N}` local
// twice -- generated code that does not compile.

public record FirstValidated(string Name);

public record SecondValidated(string Name);

[WolverineIgnore]
public static class FirstValidatedHandler
{
    public static IEnumerable<string> Validate(FirstValidated command)
    {
        if (string.IsNullOrEmpty(command.Name)) yield return "Name is required";
    }

    public static void Handle(FirstValidated command)
    {
    }
}

[WolverineIgnore]
public static class SecondValidatedHandler
{
    public static IEnumerable<string> Validate(SecondValidated command)
    {
        if (string.IsNullOrEmpty(command.Name)) yield return "Name is required";
    }

    public static void Handle(SecondValidated command)
    {
    }
}

public class continuation_variable_naming_4714
{
    private static Variable continuationVariable() =>
        new(typeof(HandlerContinuation), "handlerContinuation");

    [Fact]
    public void the_first_frame_in_a_chain_keeps_the_bare_name()
    {
        var variable = continuationVariable();
        _ = new SimpleValidationHandlerFrame(variable, 0);

        // Index 0 takes the bare name, so the overwhelmingly common single-frame chain generates a stable
        // `handlerContinuation` forever rather than whatever number the process happened to be on.
        variable.Usage.ShouldBe("handlerContinuation");
    }

    [Fact]
    public void later_frames_in_the_same_chain_are_disambiguated()
    {
        var second = continuationVariable();
        _ = new SimpleValidationHandlerFrame(second, 1);

        // The suffix still has to exist -- two frames of this kind in one generated method would otherwise
        // collide on the same local name. That is what the static counter was for.
        second.Usage.ShouldBe("handlerContinuation1");
    }

    [Fact]
    public void the_index_does_not_depend_on_how_many_frames_were_built_before()
    {
        // THE GH-4714 defect, stated directly: build a pile of frames, then build the "first" frame of a
        // fresh chain. Under the static counter this came back as handlerContinuation21.
        for (var i = 0; i < 20; i++)
        {
            _ = new SimpleValidationHandlerFrame(continuationVariable(), i);
        }

        var fresh = continuationVariable();
        _ = new SimpleValidationHandlerFrame(fresh, 0);

        fresh.Usage.ShouldBe("handlerContinuation");
    }

    /// <summary>
    /// The property the issue is actually about, through the real bootstrap rather than the constructors:
    /// two chains in one host, each with one validation frame, must BOTH be the first in their own chain.
    /// Under the static counter the second chain compiled came out one higher than the first, and which
    /// number either got depended on everything compiled before them.
    /// </summary>
    [Fact]
    public async Task two_chains_in_one_host_each_number_from_zero()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(continuation_variable_naming_4714).Assembly;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FirstValidatedHandler))
                    .IncludeType(typeof(SecondValidatedHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();

        // Force codegen on both -- attributes and middleware frames are only applied when the source is
        // actually generated.
        runtime.Handlers.HandlerFor<FirstValidated>();
        runtime.Handlers.HandlerFor<SecondValidated>();

        var first = sourceCodeFor(runtime.Handlers.ChainFor<FirstValidated>()!);
        var second = sourceCodeFor(runtime.Handlers.ChainFor<SecondValidated>()!);

        // The validation outcome variable, as the generator actually names it
        first.ShouldContain("var stringValueIEnumerable =");
        second.ShouldContain("var stringValueIEnumerable =");

        // Neither chain carries a suffix, because each is the FIRST frame in its own method. Under the
        // static counter one came out stringValueIEnumerable1 and the other stringValueIEnumerable2 -- or 7
        // and 8, depending entirely on what else the process had compiled first.
        Regex.IsMatch(first, @"stringValueIEnumerable\d").ShouldBeFalse(first);
        Regex.IsMatch(second, @"stringValueIEnumerable\d").ShouldBeFalse(second);
    }

    private static string sourceCodeFor(HandlerChain chain)
    {
        return chain.SourceCode ?? string.Empty;
    }

    [Fact]
    public void every_frame_family_numbers_the_same_way()
    {
        var simple = continuationVariable();
        var requirement = continuationVariable();

        _ = new SimpleValidationHandlerFrame(simple, 0);
        _ = new RequirementResultHandlerFrame(requirement, 0);

        simple.Usage.ShouldBe("handlerContinuation");
        requirement.Usage.ShouldBe("handlerContinuation");
    }
}
