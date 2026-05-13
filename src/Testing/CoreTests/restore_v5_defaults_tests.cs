using JasperFx.CodeGeneration.Model;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests;

/// <summary>
/// Coverage for <see cref="WolverineOptions.RestoreV5Defaults"/>. The method's
/// body is short (one line at the time of writing) but high-stakes — it's the
/// documented escape hatch in the 6.0 migration guide for users who want
/// Wolverine 6's API surface without 6's runtime defaults.
///
/// These tests serve two purposes:
///
///   1. Pin the 6.0 defaults that <see cref="WolverineOptions.RestoreV5Defaults"/>
///      claims to flip back. If a default changes in 6.x without a matching
///      line in RestoreV5Defaults, the second assert here fires and the test
///      flags the drift to the contributor.
///
///   2. Document, by example, what the method does and doesn't do.
///      Specifically: NewtonsoftSerializer is NOT restored as the default
///      because System.Text.Json has been the default since 5.0 — the test
///      below asserts that DefaultSerializer doesn't change.
/// </summary>
public class restore_v5_defaults_tests
{
    [Fact]
    public void RestoreV5Defaults_flips_ServiceLocationPolicy_to_AllowedButWarn()
    {
        var options = new WolverineOptions();

        // Sanity-pin the 6.0 default first. If this assertion fails, it
        // means the 6.x default was changed in core without also updating
        // the at-a-glance table in the migration guide and the comment
        // block on RestoreV5Defaults. Update all three together.
        options.ServiceLocationPolicy.ShouldBe(ServiceLocationPolicy.NotAllowed);

        options.RestoreV5Defaults();

        options.ServiceLocationPolicy.ShouldBe(ServiceLocationPolicy.AllowedButWarn);
    }

    [Fact]
    public void RestoreV5Defaults_does_not_change_the_default_serializer()
    {
        // System.Text.Json has been the Wolverine default since 5.0
        // (UseSystemTextJsonForSerialization() is wired in the
        // WolverineOptions constructor). There is no Newtonsoft default
        // to "restore" — that was a Wolverine 4.x-and-earlier default.
        // Users who want Newtonsoft must install WolverineFx.Newtonsoft
        // and call UseNewtonsoftForSerialization() explicitly; this test
        // documents that RestoreV5Defaults() is not a shortcut for it.
        var options = new WolverineOptions();
        var defaultBeforeRestore = options.DefaultSerializer;

        options.RestoreV5Defaults();

        options.DefaultSerializer.ShouldBeSameAs(defaultBeforeRestore);
    }
}
