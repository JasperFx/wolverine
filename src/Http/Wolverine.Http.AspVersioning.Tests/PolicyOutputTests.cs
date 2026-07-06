using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Shouldly;

namespace Wolverine.Http.AspVersioning.Tests;

// ---------- Fixture handlers ----------

// One chain per provider role, all sharing route "/roles" so they land in one group/set.
[ApiVersion("1.0")]
internal class ServedRoleHandler
{
    [WolverineGet("/roles")]
    public string Served() => "served";
}

[ApiVersion("2.0", Deprecated = true)]
internal class DeprecatedRoleHandler
{
    [WolverineGet("/roles")]
    public string Deprecated() => "deprecated";
}

[ApiVersion("3.0")]
internal class MappedRoleHandler
{
    [MapToApiVersion("3.0")]
    [WolverineGet("/roles")]
    public string Mapped() => "mapped";
}

[AdvertiseApiVersions("4.0")]
internal class AdvertisedRoleHandler
{
    [WolverineGet("/roles")]
    public string Advertised() => "advertised";
}

[AdvertiseApiVersions("5.0", Deprecated = true)]
internal class AdvertisedDeprecatedRoleHandler
{
    [WolverineGet("/roles")]
    public string AdvertisedDeprecated() => "advertised-deprecated";
}

[ApiVersionNeutral]
internal class NeutralRoleHandler
{
    [WolverineGet("/neutral-role")]
    public string Get() => "neutral";
}

internal class UnversionedRoleHandler
{
    [WolverineGet("/unversioned-role")]
    public string Get() => "unversioned";
}

[ApiVersion("1.0")]
internal class IdempotencyHandler
{
    [WolverineGet("/idempotency")]
    public string Get() => "v1";
}

// Pre-existing unrelated metadata for the non-destructive check.
internal sealed class UnrelatedMetadataMarker;

[ApiVersion("1.0")]
internal class TagsProbeHandler
{
    [WolverineGet("/tags-probe")]
    public string Get() => "v1";
}

[ApiVersion("1.0")]
[ApiVersion("2.0")]
internal class UniqueOperationIdHandler
{
    [MapToApiVersion("1.0")]
    [WolverineGet("/unique-operation-id")]
    public string GetV1() => "v1";

    [MapToApiVersion("2.0")]
    [WolverineGet("/unique-operation-id")]
    public string GetV2() => "v2";
}

// ---------- Tests ----------

/// <summary>
/// Runs the full <see cref="AspVersioningPolicy"/> over a realistic set of chains and asserts on the
/// metadata that gets attached. Stops at "the correct metadata is present"; does not assert how the
/// matcher later interprets it.
/// </summary>
public class PolicyOutputTests : HostlessAspVersioningContext
{
    public PolicyOutputTests(HostlessAspVersioningFixture fixture) : base(fixture) { }

    // Per-chain provider roles land in the right ApiVersionModel buckets. Advertised versions fold into
    // Supported (advertised-deprecated into Deprecated) — ApiVersionModel has no separate advertised list
    // — so the advertised role shows up as bucket membership that is absent from DeclaredApiVersions.
    [Fact]
    public void per_chain_provider_roles_are_correct()
    {
        var served = VersioningHarness.ChainFor<ServedRoleHandler>(x => x.Served());
        var deprecated = VersioningHarness.ChainFor<DeprecatedRoleHandler>(x => x.Deprecated());
        var mapped = VersioningHarness.ChainFor<MappedRoleHandler>(x => x.Mapped());
        var advertised = VersioningHarness.ChainFor<AdvertisedRoleHandler>(x => x.Advertised());
        var advertisedDeprecated = VersioningHarness.ChainFor<AdvertisedDeprecatedRoleHandler>(x =>
            x.AdvertisedDeprecated()
        );

        VersioningHarness.Apply(served, deprecated, mapped, advertised, advertisedDeprecated);

        // served → None role: implemented + supported
        var servedModel = served.EndpointModel();
        servedModel.SupportedApiVersions.ShouldContain(new ApiVersion(1, 0));
        servedModel.ImplementedApiVersions.ShouldContain(new ApiVersion(1, 0));

        // served-deprecated → Deprecated role
        deprecated.EndpointModel().DeprecatedApiVersions.ShouldContain(new ApiVersion(2, 0));

        // mapped → Mapped role
        mapped.VersionMetadata()!.IsMappedTo(new ApiVersion(3, 0)).ShouldBeTrue();

        // advertised → folds into the supported bucket but is NOT declared
        advertised.EndpointModel().SupportedApiVersions.ShouldContain(new ApiVersion(4, 0));
        advertised.EndpointModel().DeclaredApiVersions.ShouldNotContain(new ApiVersion(4, 0));

        // advertised-deprecated → folds into the deprecated bucket, likewise not declared
        advertisedDeprecated
            .EndpointModel()
            .DeprecatedApiVersions.ShouldContain(new ApiVersion(5, 0));
        advertisedDeprecated
            .EndpointModel()
            .DeclaredApiVersions.ShouldNotContain(new ApiVersion(5, 0));
    }

    // A neutral chain gets neutral metadata.
    [Fact]
    public void neutral_chain_has_neutral_metadata()
    {
        var chain = VersioningHarness.ChainFor<NeutralRoleHandler>(x => x.Get());

        VersioningHarness.Apply(chain);

        chain.VersionMetadata()!.IsApiVersionNeutral.ShouldBeTrue();
    }

    // An unversioned chain is left untouched — no version metadata.
    [Fact]
    public void unversioned_chain_is_untouched()
    {
        var chain = VersioningHarness.ChainFor<UnversionedRoleHandler>(x => x.Get());

        VersioningHarness.Apply(chain);

        chain.VersionMetadata().ShouldBeNull();
    }

    // Applying the policy is idempotent and non-destructive: exactly one ApiVersionMetadata after a
    // re-run (a single apply is the baseline), and pre-existing unrelated metadata survives. Exercises
    // the policy's _processedChains guard.
    [Fact]
    public void rerunning_apply_is_idempotent_and_non_destructive()
    {
        var chain = VersioningHarness.ChainFor<IdempotencyHandler>(x => x.Get());

        var marker = new UnrelatedMetadataMarker();
        chain.Metadata.WithMetadata(marker);

        VersioningHarness.ApplyRepeated(2, chain);

        chain.VersionMetadataCount().ShouldBe(1);
        chain.MetadataOf<UnrelatedMetadataMarker>().ShouldContain(marker);
    }

    // The bridge builds the shared set with a null name, so no version-set-derived TagsAttribute is
    // injected (a named set would add TagsAttribute(name) and pollute OpenAPI tags). Host-free because
    // Wolverine's baseline tags differ from a native minimal-API endpoint's — only the absence of a
    // version-set-derived tag is meaningful.
    [Fact]
    public void no_version_set_derived_tags_are_attached()
    {
        var chain = VersioningHarness.ChainFor<TagsProbeHandler>(x => x.Get());

        VersioningHarness.Apply(chain);

        chain.TagMetadata().ShouldBeEmpty();
    }

    [Fact]
    public void application_services_are_enabled_on_versioned_chains()
    {
        var versionedChain = VersioningHarness.ChainFor<ServedRoleHandler>(x => x.Served());
        var unversionedChain = VersioningHarness.ChainFor<UnversionedRoleHandler>(x => x.Get());

        VersioningHarness.Apply(versionedChain, unversionedChain);
        
        versionedChain.RequiresApplicationServices.ShouldBeTrue();
        unversionedChain.RequiresApplicationServices.ShouldBeFalse();
    }

    [Fact]
    public void conflicting_chains_have_unique_operation_ids()
    {
        var v1Chain = VersioningHarness.ChainFor<UniqueOperationIdHandler>(x => x.GetV1());
        var v2Chain = VersioningHarness.ChainFor<UniqueOperationIdHandler>(x => x.GetV2());

        VersioningHarness.Apply(v1Chain, v2Chain);

        v1Chain.HasExplicitOperationId.ShouldBeTrue();
        v2Chain.HasExplicitOperationId.ShouldBeTrue();
        v1Chain.OperationId.ShouldNotBe(v2Chain.OperationId);
    }
}
