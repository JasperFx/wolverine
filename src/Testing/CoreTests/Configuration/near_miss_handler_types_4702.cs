using CoreTests.Acceptance;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Xunit;

namespace CoreTests.Configuration;

// GH-4702. Conventional discovery matches the "Handler"/"Consumer" suffix exactly, so a plural-named class
// never enters the handler query and its Handle methods are never seen. Wolverine's own test suite had real
// instances of this: SRMessageHandlers, duplicated across seven transport smoke tests, whose four Handle
// methods had never run. This warning is what found them, and GH-4708 deleted them.
//
// The asymmetry that makes it easy to hit: plural IS accepted at the method level -- Handles and Consumes
// are both valid handler method names -- just not at the type level.

public class near_miss_handler_types_4702
{
    // Each of these gets its own message type on purpose: a shared one would build a multi-handler chain in
    // every other host bootstrapped from this assembly.
    public record NearMissed;

    public record CorrectlyNamed;

    public record Attributed;

    public record Ignored;

    public record NotHandlerShaped;

    // The trap itself
    public class NearMissedHandlers
    {
        public void Handle(NearMissed message)
        {
        }
    }

    // The control: identical but singular, so discovery takes it
    public class CorrectlyNamedHandler
    {
        public void Handle(CorrectlyNamed message)
        {
        }
    }

    // Plural but discovered anyway. This is the shape of our own documented ValidMessageHandlers sample, and
    // warning about it would be actively wrong.
    [WolverineHandler]
    public class AttributedHandlers
    {
        public void Handle(Attributed message)
        {
        }
    }

    // Opted out on purpose. Renaming would not be the advice.
    [WolverineIgnore]
    public class IgnoredHandlers
    {
        public void Handle(Ignored message)
        {
        }
    }

    // Plural, but nothing here is a handler method, so there is no near miss to report
    public class NotHandlerShapedHandlers
    {
        public void SomethingElse(NotHandlerShaped message)
        {
        }
    }

    private static (Type[] discovered, IReadOnlyList<Type> nearMisses) run(
        Action<HandlerDiscovery>? configure = null)
    {
        var discovery = new HandlerDiscovery();
        configure?.Invoke(discovery);

        // FindCalls is what runs specifyConventionalHandlerDiscovery(), so the near-miss query has to come
        // after it -- exactly the order HandlerGraph.compileWithRuntimeScanning uses.
        var discovered = discovery
            .FindCalls(new WolverineOptions { ApplicationAssembly = typeof(near_miss_handler_types_4702).Assembly })
            .Select(x => x.Item1)
            .Distinct()
            .ToArray();

        return (discovered, discovery.FindNearMissHandlerTypes());
    }

    [Fact]
    public void a_plural_named_type_with_handler_methods_is_a_near_miss()
    {
        var (discovered, nearMisses) = run();

        // The negative control, and the fact the warning exists to report: this type really is invisible.
        discovered.ShouldNotContain(typeof(NearMissedHandlers));

        nearMisses.ShouldContain(typeof(NearMissedHandlers));
    }

    [Fact]
    public void the_singular_name_is_discovered_and_is_not_a_near_miss()
    {
        var (discovered, nearMisses) = run();

        discovered.ShouldContain(typeof(CorrectlyNamedHandler));
        nearMisses.ShouldNotContain(typeof(CorrectlyNamedHandler));
    }

    [Fact]
    public void a_plural_type_discovered_by_another_convention_is_not_a_near_miss()
    {
        var (discovered, nearMisses) = run();

        discovered.ShouldContain(typeof(AttributedHandlers));
        nearMisses.ShouldNotContain(typeof(AttributedHandlers));
    }

    [Fact]
    public void an_explicitly_ignored_plural_type_is_not_a_near_miss()
    {
        var (discovered, nearMisses) = run();

        discovered.ShouldNotContain(typeof(IgnoredHandlers));
        nearMisses.ShouldNotContain(typeof(IgnoredHandlers));
    }

    [Fact]
    public void a_plural_type_without_handler_methods_is_not_a_near_miss()
    {
        var (_, nearMisses) = run();

        nearMisses.ShouldNotContain(typeof(NotHandlerShapedHandlers));
    }

    [Fact]
    public void an_explicitly_included_plural_type_is_not_a_near_miss()
    {
        var (discovered, nearMisses) = run(x => x.IncludeType<NearMissedHandlers>());

        discovered.ShouldContain(typeof(NearMissedHandlers));
        nearMisses.ShouldNotContain(typeof(NearMissedHandlers));
    }

    [Fact]
    public void a_plural_type_matched_by_a_custom_convention_is_not_a_near_miss()
    {
        var (discovered, nearMisses) = run(x =>
            x.CustomizeHandlerDiscovery(q => q.Includes.WithNameSuffix("Handlers")));

        discovered.ShouldContain(typeof(NearMissedHandlers));
        nearMisses.ShouldNotContain(typeof(NearMissedHandlers));
    }

    [Fact]
    public void nothing_is_a_near_miss_when_conventional_discovery_is_disabled()
    {
        // Every concrete type in the assembly is unmatched here, so reporting near misses would be pure
        // noise against a user who asked for explicit control.
        var (_, nearMisses) = run(x => x.DisableConventionalDiscovery());

        nearMisses.ShouldBeEmpty();
    }

    /// <summary>
    /// The unit facts above prove the query; this proves the query is actually wired to a log statement and
    /// that the message names the type and the fix. Without it, an inert foreach would pass everything else.
    /// </summary>
    [Fact]
    public async Task the_warning_reaches_the_log_on_a_real_bootstrap()
    {
        var logger = new RecordingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logger))
            .UseWolverine(opts => { opts.ApplicationAssembly = typeof(near_miss_handler_types_4702).Assembly; })
            .StartAsync(TestContext.Current.CancellationToken);

        var matching = logger.Warnings.Where(x => x.Contains(nameof(NearMissedHandlers))).ToArray();

        // Emitted once per type, so exactly one line names it. The warnings actually seen are worth printing:
        // a zero here is either the wiring gone (the warning is never reached) or the scan gone (the host took
        // the static-registry branch), and the other warnings tell you which.
        matching.Length.ShouldBe(1, $"Warnings seen: [{logger.Warnings.Join(" | ")}]");

        var warning = matching[0];
        warning.ShouldContain("has handler-shaped methods but was not discovered");

        // The three escapes the user actually has, and the rename suggestion has to drop the plural
        warning.ShouldContain("NearMissedHandler,");
        warning.ShouldContain("[WolverineHandler]");
        warning.ShouldContain("IncludeType");
    }
}
