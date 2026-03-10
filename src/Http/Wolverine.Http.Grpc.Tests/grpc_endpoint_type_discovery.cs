using Shouldly;
using Wolverine.Http.Grpc;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Unit tests for <see cref="GrpcEndpointSource"/> type-eligibility logic.
///
/// No web host is required — these tests exercise pure type-metadata checks against
/// the <c>IsGrpcEndpointType</c> and <c>FindGrpcEndpointTypes</c> methods.
/// They mirror the style used in Wolverine.Http.Tests/endpoint_discovery_and_construction.cs
/// but operate one level lower: at the raw type-filter layer rather than at the
/// fully-bootstrapped routing layer.
/// </summary>
public class grpc_endpoint_type_discovery
{
    // ---------------------------------------------------------------------------
    // IsGrpcEndpointType — eligible types (expect true)
    // ---------------------------------------------------------------------------

    [Fact]
    public void type_with_attribute_and_no_naming_convention_is_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_ValidByAttribute)).ShouldBeTrue();

    [Fact]
    public void type_with_GrpcEndpoint_suffix_is_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_ValidGrpcEndpoint)).ShouldBeTrue();

    [Fact]
    public void type_with_GrpcEndpoints_suffix_is_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_ValidGrpcEndpoints)).ShouldBeTrue();

    [Fact]
    public void type_with_GrpcService_suffix_is_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_ValidGrpcService)).ShouldBeTrue();

    [Fact]
    public void type_with_GrpcServices_suffix_is_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_ValidGrpcServices)).ShouldBeTrue();

    // ---------------------------------------------------------------------------
    // IsGrpcEndpointType — ineligible types (expect false)
    // ---------------------------------------------------------------------------

    [Fact]
    public void type_without_attribute_and_without_matching_suffix_is_not_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_InvalidNoMatch)).ShouldBeFalse();

    [Fact]
    public void type_without_base_class_is_not_eligible_even_with_attribute()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_InvalidNoBaseClass)).ShouldBeFalse();

    [Fact]
    public void abstract_type_is_not_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_InvalidAbstract)).ShouldBeFalse();

    [Fact]
    public void interface_is_not_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(ITypeDiscovery_InvalidInterface)).ShouldBeFalse();

    [Fact]
    public void open_generic_type_definition_is_not_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_InvalidGenericGrpcEndpoint<>)).ShouldBeFalse();

    // ---------------------------------------------------------------------------
    // FindGrpcEndpointTypes — assembly scanning
    // ---------------------------------------------------------------------------

    [Fact]
    public void find_grpc_endpoint_types_returns_empty_for_no_assemblies()
    {
        var types = GrpcEndpointSource.FindGrpcEndpointTypes([]);
        types.ShouldBeEmpty();
    }

    [Fact]
    public void find_grpc_endpoint_types_discovers_attributed_types()
    {
        var types = GrpcEndpointSource.FindGrpcEndpointTypes([typeof(grpc_endpoint_type_discovery).Assembly]);
        types.ShouldContain(typeof(TypeDiscovery_ValidByAttribute));
        types.ShouldContain(typeof(BootstrapAttributedGrpcService));
    }

    [Fact]
    public void find_grpc_endpoint_types_discovers_all_convention_suffix_types()
    {
        var types = GrpcEndpointSource.FindGrpcEndpointTypes([typeof(grpc_endpoint_type_discovery).Assembly]);
        types.ShouldContain(typeof(TypeDiscovery_ValidGrpcEndpoint));
        types.ShouldContain(typeof(TypeDiscovery_ValidGrpcEndpoints));
        types.ShouldContain(typeof(TypeDiscovery_ValidGrpcService));
        types.ShouldContain(typeof(TypeDiscovery_ValidGrpcServices));
    }

    [Fact]
    public void find_grpc_endpoint_types_excludes_ineligible_types()
    {
        var types = GrpcEndpointSource.FindGrpcEndpointTypes([typeof(grpc_endpoint_type_discovery).Assembly]);
        types.ShouldNotContain(typeof(TypeDiscovery_InvalidNoMatch));
        types.ShouldNotContain(typeof(TypeDiscovery_InvalidNoBaseClass));
        types.ShouldNotContain(typeof(TypeDiscovery_InvalidAbstract));
        types.ShouldNotContain(typeof(ITypeDiscovery_InvalidInterface));
        types.ShouldNotContain(typeof(TypeDiscovery_InvalidGenericGrpcEndpoint<>));
    }

    [Fact]
    public void find_grpc_endpoint_types_deduplicates_the_same_assembly_supplied_multiple_times()
    {
        var assembly = typeof(grpc_endpoint_type_discovery).Assembly;
        var types = GrpcEndpointSource.FindGrpcEndpointTypes([assembly, assembly]);

        // All returned types should be distinct — no duplicates from repeated assemblies.
        types.Count.ShouldBe(types.Distinct().Count());
    }

    [Fact]
    public void find_grpc_endpoint_types_same_count_regardless_of_duplicate_assembly_entries()
    {
        var assembly = typeof(grpc_endpoint_type_discovery).Assembly;
        var singleScan = GrpcEndpointSource.FindGrpcEndpointTypes([assembly]);
        var doubleScan = GrpcEndpointSource.FindGrpcEndpointTypes([assembly, assembly]);

        doubleScan.Count.ShouldBe(singleScan.Count);
    }
}
