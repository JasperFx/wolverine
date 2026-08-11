using System.Reflection;
using Shouldly;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-3907 decision 2: <b>no type in Wolverine core may share a simple name with a public type in a
/// <c>Wolverine.&lt;Store&gt;</c> integration.</b>
///
/// <para>
/// As the aggregate handler workflow is pulled down out of <c>Wolverine.Marten</c> and
/// <c>Wolverine.Polecat</c> into core, every type that moves is a chance to collide with the public
/// vocabulary the integrations keep — 36 type names are public in <i>both</i> integrations today
/// (<c>WriteAggregateAttribute</c>, <c>Events</c>, <c>ConcurrencyStyle</c>, <c>UnknownAggregateException</c>,
/// and friends). A store integration has <c>InternalsVisibleTo</c> from core, so a core type sharing a
/// name with one of those is a <c>CS0104</c> ambiguous reference for every user file that imports both
/// namespaces.
/// </para>
///
/// <para>
/// The issue asks for this to be enforced by a reflection test "so it stays true rather than being
/// rediscovered at compile time". Each store's suite enrolls its own assembly, which is also how
/// <c>Wolverine.Fisher</c> picks the invariant up for free when it arrives.
/// </para>
/// </summary>
public abstract class CoreTypeNameCollisionCompliance
{
    /// <summary>
    /// The store integration assembly under test, e.g. <c>typeof(WriteAggregateAttribute).Assembly</c>.
    /// </summary>
    protected abstract Assembly StoreAssembly { get; }

    /// <summary>
    /// Simple names that are knowingly shared and tolerated. <c>TestingExtensions</c> is the one
    /// pre-existing collision called out in GH-3907; it predates this rule and is not made worse by it.
    /// Anything added here needs a reason, because each entry is a name users cannot import cleanly
    /// from both namespaces at once.
    /// </summary>
    protected virtual ISet<string> KnownAndTolerated { get; } =
        new HashSet<string> { "TestingExtensions" };

    [Fact]
    public void no_core_type_shares_a_simple_name_with_a_public_store_type()
    {
        var coreTypeNames = typeof(WolverineOptions).Assembly
            .GetTypes()
            .Select(x => x.Name)
            .ToHashSet();

        var collisions = StoreAssembly
            .GetExportedTypes()
            .Select(x => x.Name)
            .Where(coreTypeNames.Contains)
            .Where(x => !KnownAndTolerated.Contains(x))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        collisions.ShouldBeEmpty(
            $"These public {StoreAssembly.GetName().Name} type names also exist in Wolverine core, so any file " +
            $"importing both namespaces gets CS0104: {string.Join(", ", collisions)}. Rename the core type — the " +
            "store integration's public vocabulary is the side that has to stay put.");
    }
}
