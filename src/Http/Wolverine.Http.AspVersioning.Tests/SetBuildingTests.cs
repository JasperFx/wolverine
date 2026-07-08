using Asp.Versioning;
using Shouldly;

namespace Wolverine.Http.AspVersioning.Tests;

// ---------- Fixture handlers ----------

[ApiVersion("1.0")]
[ApiVersion("2.0", Deprecated = true)]
internal class MixedSupportedDeprecatedHandler
{
    [WolverineGet("/mixed")]
    public string Get() => "mixed";
}

[ApiVersion("1.0")]
internal class SupportedSiblingHandler
{
    [WolverineGet("/supported-wins")]
    public string Get() => "supported";
}

[ApiVersion("1.0", Deprecated = true)]
internal class DeprecatedSiblingHandler
{
    [WolverineGet("/supported-wins")]
    public string Get() => "deprecated";
}

[AdvertiseApiVersions("3.0")]
[AdvertiseApiVersions("4.0", Deprecated = true)]
internal class AdvertisedFoldHandler
{
    [WolverineGet("/advertised-fold")]
    public string Get() => "advertised";
}

[ApiVersionNeutral]
internal class NeutralSetGetHandler
{
    [WolverineGet("/all-neutral")]
    public string Get() => "neutral-1";
}

[ApiVersionNeutral]
internal class NeutralSetPostHandler
{
    [WolverinePost("/all-neutral")]
    public string Post() => "neutral-2";
}

internal class UnversionedSetHandler
{
    [WolverineGet("/unversioned-set")]
    public string Get() => "unversioned";
}

[ApiVersion("1.0")]
internal class DuplicateFirstHandler
{
    [WolverineGet("/duplicate")]
    public string Get() => "first";
}

[ApiVersion("1.0")]
internal class DuplicateSecondHandler
{
    [WolverineGet("/duplicate")]
    public string Get() => "second";
}

// ---------- Tests ----------

/// <summary>
/// Bucketing into supported/deprecated and construction of the per-group
/// <see cref="Asp.Versioning.Builder.ApiVersionSet"/>. Single-chain bucketing lives in
/// <see cref="VersionedChain"/>; cross-chain set decisions live in <see cref="AspVersioningPolicy"/>
/// and are observed through the attached set/metadata.
/// </summary>
public class SetBuildingTests : HostlessAspVersioningContext
{
    public SetBuildingTests(HostlessAspVersioningFixture fixture) : base(fixture) { }

    // A chain serving 1.0 and deprecated-2.0 partitions into the two buckets on the VersionedChain.
    [Fact]
    public void supported_and_deprecated_partition_correctly()
    {
        var chain = VersioningHarness.ChainFor<MixedSupportedDeprecatedHandler>(x => x.Get());

        var vc = VersionedChain.FromHttpChain(chain);
        vc.Supported.Select(r => r.Version).ShouldBe(new[] { new ApiVersion(1, 0) });
        vc.Deprecated.Select(r => r.Version).ShouldBe(new[] { new ApiVersion(2, 0) });
    }

    // Same version supported by one chain and deprecated by another → supported wins; it appears only in
    // the supported bucket (matches Asp.Versioning's own deprecated.ExceptWith(supported) merge).
    [Fact]
    public void supported_wins_over_deprecated_for_same_version()
    {
        var supported = VersioningHarness.ChainFor<SupportedSiblingHandler>(x => x.Get());
        var deprecated = VersioningHarness.ChainFor<DeprecatedSiblingHandler>(x => x.Get());

        VersioningHarness.Apply(supported, deprecated);

        var group = supported.GroupModel();
        group.SupportedApiVersions.ShouldContain(new ApiVersion(1, 0));
        group.DeprecatedApiVersions.ShouldNotContain(new ApiVersion(1, 0));
    }

    // Advertised folds into the supported/deprecated buckets. Asp.Versioning's read-side ApiVersionModel
    // has no separate advertised list, so the one signal that 3.0/4.0 were advertised (not declared) is
    // their absence from DeclaredApiVersions.
    [Fact]
    public void advertised_folds_into_supported_and_deprecated_buckets()
    {
        var chain = VersioningHarness.ChainFor<AdvertisedFoldHandler>(x => x.Get());

        VersioningHarness.Apply(chain);

        var group = chain.GroupModel();
        group.SupportedApiVersions.ShouldContain(new ApiVersion(3, 0));
        group.DeprecatedApiVersions.ShouldContain(new ApiVersion(4, 0));
        group.DeclaredApiVersions.ShouldNotContain(new ApiVersion(3, 0));
        group.DeclaredApiVersions.ShouldNotContain(new ApiVersion(4, 0));
    }

    // An all-neutral group builds no set; each chain gets neutral metadata.
    [Fact]
    public void all_neutral_group_builds_no_set()
    {
        var first = VersioningHarness.ChainFor<NeutralSetGetHandler>(x => x.Get());
        var second = VersioningHarness.ChainFor<NeutralSetPostHandler>(x => x.Post());

        VersioningHarness.Apply(first, second);

        first.VersionMetadata()!.IsApiVersionNeutral.ShouldBeTrue();
        second.VersionMetadata()!.IsApiVersionNeutral.ShouldBeTrue();
    }

    // An unversioned chain is filtered out before the policy attaches anything.
    [Fact]
    public void unversioned_group_builds_no_metadata()
    {
        var chain = VersioningHarness.ChainFor<UnversionedSetHandler>(x => x.Get());

        VersioningHarness.Apply(chain);

        chain.VersionMetadata().ShouldBeNull();
    }

    // Duplicate identical contributions collapse to one entry without error, so the supported/deprecated
    // fold is only needed for the conflict case (supported_wins_over_deprecated), not plain dedup.
    [Fact]
    public void duplicate_identical_contributions_collapse_without_error()
    {
        var first = VersioningHarness.ChainFor<DuplicateFirstHandler>(x => x.Get());
        var second = VersioningHarness.ChainFor<DuplicateSecondHandler>(x => x.Get());

        Should.NotThrow(() => VersioningHarness.Apply(first, second));

        first.GroupModel().SupportedApiVersions.ShouldBe(new[] { new ApiVersion(1, 0) });
    }
}
