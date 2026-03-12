using Shouldly;
using Wolverine.Http.Grpc;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Unit tests for GrpcEndpointSource type-eligibility logic.
/// Tests the IsGrpcEndpointType and FindGrpcEndpointTypes methods without requiring a web host.
/// </summary>
public class grpc_endpoint_type_discovery
{
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

    [Fact]
    public void type_with_attribute_and_no_base_class_is_eligible_proto_first_scenario()
    {
        // Proto-first services inherit proto-generated base classes, not WolverineGrpcEndpointBase
        GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_ValidAttributeOnlyNoBaseClass))
            .ShouldBeTrue();
    }

    [Fact]
    public void type_without_attribute_and_without_matching_suffix_is_not_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_InvalidNoMatch)).ShouldBeFalse();

    [Fact]
    public void abstract_type_is_not_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_InvalidAbstract)).ShouldBeFalse();

    [Fact]
    public void interface_is_not_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(ITypeDiscovery_InvalidInterface)).ShouldBeFalse();

    [Fact]
    public void open_generic_type_definition_is_not_eligible()
        => GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_InvalidGenericGrpcEndpoint<>)).ShouldBeFalse();

    [Fact]
    public void convention_suffix_without_base_class_and_without_attribute_is_not_eligible()
    {
        // Convention discovery requires WolverineGrpcEndpointBase as a safety guard to avoid false positives
        GrpcEndpointSource.IsGrpcEndpointType(typeof(TypeDiscovery_InvalidConventionSuffixWithoutBaseClass))
            .ShouldBeFalse();
    }

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
    public void find_grpc_endpoint_types_discovers_attributed_type_without_base_class()
    {
        // Proto-first services use [WolverineGrpcService] without inheriting WolverineGrpcEndpointBase
        var types = GrpcEndpointSource.FindGrpcEndpointTypes([typeof(grpc_endpoint_type_discovery).Assembly]);
        types.ShouldContain(typeof(TypeDiscovery_ValidAttributeOnlyNoBaseClass));
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
        types.ShouldNotContain(typeof(TypeDiscovery_InvalidAbstract));
        types.ShouldNotContain(typeof(ITypeDiscovery_InvalidInterface));
        types.ShouldNotContain(typeof(TypeDiscovery_InvalidGenericGrpcEndpoint<>));
        types.ShouldNotContain(typeof(TypeDiscovery_InvalidConventionSuffixWithoutBaseClass));
    }

    [Fact]
    public void find_grpc_endpoint_types_deduplicates_the_same_assembly_supplied_multiple_times()
    {
        var assembly = typeof(grpc_endpoint_type_discovery).Assembly;
        var types = GrpcEndpointSource.FindGrpcEndpointTypes([assembly, assembly]);

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
