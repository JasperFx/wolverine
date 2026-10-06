using System.Reflection;
using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
///     GH-4825. <c>PrepopulateRoutingCache</c> walks every message type the handler graph discovered, and
///     <c>RoutingFor</c>'s cache-miss path closes <c>MessageRouter&lt;T&gt;</c> / <c>EmptyMessageRouter&lt;T&gt;</c>
///     over each one reflectively. An application with a durable message store has the framework's own agent
///     messages in that set — and they are <c>internal</c>, so
///     <c>HandlerRegistryCodeFile.onlyPublic()</c> drops them and the emitted <c>[DynamicDependency]</c>
///     rooting block <b>cannot</b> name them: generated code cannot put a non-public type inside a
///     <c>typeof()</c>. In a native image ILC has therefore trimmed exactly those instantiations.
///
///     <para>Direct construction in <c>_frameworkRouterFactories</c> is the only fix available, which is
///     what GH-4287 already did for <see cref="IAgentCommand" /> itself.</para>
/// </summary>
/// <remarks>
///     Asserted as COVERAGE of the real set rather than as a list of the eight types that were missing.
///     A list would be satisfied the day it was written and say nothing when the next agent message is
///     added — and the cost of missing one is a native image that fails at startup, which no analyzer
///     reports and only a Balanced-mode publish reproduces.
/// </remarks>
public class framework_router_factories_cover_every_agent_message
{
    [Fact]
    public void every_internal_agent_message_has_a_directly_constructed_router_factory()
    {
        var factories = frameworkRouterFactoryKeys();

        // Not vacuous: if the reflection above ever stops finding the field, this fails rather than
        // passing over an empty set.
        factories.ShouldNotBeEmpty();

        var uncovered = agentMessageTypes()
            .Where(x => !factories.Contains(x))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToArray();

        uncovered.ShouldBeEmpty(
            $"These agent message types are internal, so no emitted rooting block can name them, and nothing constructs their routers directly -- a native image with a durable message store will fail in PrepopulateRoutingCache: {string.Join(", ", uncovered.Select(x => x.Name))}");
    }

    [Fact]
    public void the_agent_message_scan_actually_finds_something()
    {
        // The guard for the test above. agentMessageTypes() returning nothing would make it green while
        // covering no types at all, which is precisely the failure mode this whole class of AOT bug has.
        agentMessageTypes().ShouldNotBeEmpty();

        // And they really are non-public, which is the reason they cannot be rooted from generated code.
        foreach (var type in agentMessageTypes())
        {
            type.IsPublic.ShouldBeFalse($"{type.FullName} is public, so the rooting block could name it");
        }
    }

    private static Type[] agentMessageTypes()
    {
        return typeof(IAgentCommand).Assembly
            .GetTypes()
            .Where(x => x is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .Where(x => x.CanBeCastTo<IAgentCommand>() || x.CanBeCastTo<IDeferredAgentWork>())
            .Where(x => !x.IsPublic)
            .ToArray();
    }

    private static HashSet<Type> frameworkRouterFactoryKeys()
    {
        var field = typeof(WolverineRuntime).GetField("_frameworkRouterFactories",
            BindingFlags.NonPublic | BindingFlags.Static);

        field.ShouldNotBeNull("WolverineRuntime._frameworkRouterFactories was renamed; this test needs updating");

        var dictionary = (System.Collections.IDictionary)field!.GetValue(null)!;

        return dictionary.Keys.Cast<Type>().ToHashSet();
    }
}
