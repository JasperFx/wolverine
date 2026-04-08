using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Shouldly;
using Swashbuckle.AspNetCore.Swagger;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class openapi_metadata_customization : IntegrationContext
{
    public openapi_metadata_customization(AppFixture fixture) : base(fixture)
    {
    }

    #region OperationId tests

    [Fact]
    public void explicit_operation_id_is_used_in_openapi()
    {
        var (_, op) = FindOpenApiDocument(OperationType.Get, "/openapi/with-summary");
        op.OperationId.ShouldBe("GetWithSummary");
    }

    [Fact]
    public void explicit_operation_id_on_post()
    {
        var (_, op) = FindOpenApiDocument(OperationType.Post, "/openapi/with-operation-id");
        op.OperationId.ShouldBe("CustomPostOperation");
    }

    [Fact]
    public void default_operation_id_uses_class_and_method_name()
    {
        var (_, op) = FindOpenApiDocument(OperationType.Get, "/openapi/default-metadata");
        op.OperationId.ShouldBe(
            $"{typeof(OpenApiMetadataEndpoints).FullNameInCode()}.{nameof(OpenApiMetadataEndpoints.GetDefaultMetadata)}");
    }

    [Fact]
    public void delete_with_explicit_operation_id_in_openapi()
    {
        var (_, op) = FindOpenApiDocument(OperationType.Delete, "/openapi/with-all-metadata");
        op.OperationId.ShouldBe("DeleteWithAllMetadata");
    }

    #endregion

    #region Chain property tests

    [Fact]
    public void chain_has_explicit_operation_id_flag_when_set()
    {
        var chain = HttpChains.ChainFor("GET", "/openapi/with-summary");
        chain.ShouldNotBeNull();
        chain!.HasExplicitOperationId.ShouldBeTrue();
        chain.OperationId.ShouldBe("GetWithSummary");
    }

    [Fact]
    public void chain_does_not_have_explicit_operation_id_flag_when_not_set()
    {
        var chain = HttpChains.ChainFor("GET", "/openapi/default-metadata");
        chain.ShouldNotBeNull();
        chain!.HasExplicitOperationId.ShouldBeFalse();
    }

    [Fact]
    public void summary_metadata_is_set_on_chain()
    {
        var chain = HttpChains.ChainFor("GET", "/openapi/with-summary");
        chain!.EndpointSummary.ShouldBe("Gets a greeting with summary");
    }

    [Fact]
    public void description_metadata_is_set_on_chain()
    {
        var chain = HttpChains.ChainFor("GET", "/openapi/with-summary");
        chain!.EndpointDescription.ShouldBe(
            "This endpoint returns a simple greeting and demonstrates OpenAPI summary and description support");
    }

    [Fact]
    public void summary_is_null_when_not_set()
    {
        var chain = HttpChains.ChainFor("GET", "/openapi/default-metadata");
        chain!.EndpointSummary.ShouldBeNull();
    }

    [Fact]
    public void description_is_null_when_not_set()
    {
        var chain = HttpChains.ChainFor("GET", "/openapi/default-metadata");
        chain!.EndpointDescription.ShouldBeNull();
    }

    [Fact]
    public void delete_chain_has_all_metadata_properties()
    {
        var chain = HttpChains.ChainFor("DELETE", "/openapi/with-all-metadata");
        chain!.OperationId.ShouldBe("DeleteWithAllMetadata");
        chain.HasExplicitOperationId.ShouldBeTrue();
        chain.EndpointSummary.ShouldBe("Deletes a resource");
        chain.EndpointDescription.ShouldBe("Performs a delete operation with full OpenAPI metadata");
    }

    #endregion

    #region Endpoint metadata tests

    [Fact]
    public void endpoint_name_metadata_is_added_for_explicit_operation_id()
    {
        var endpoint = EndpointFor("/openapi/with-summary");
        var endpointName = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>();
        endpointName.ShouldNotBeNull();
        endpointName!.EndpointName.ShouldBe("GetWithSummary");
    }

    [Fact]
    public void endpoint_name_metadata_is_not_added_for_default_operation_id()
    {
        var endpoint = EndpointFor("/openapi/default-metadata");

        // Should NOT have an explicit EndpointNameMetadata added by our code,
        // which avoids duplicate endpoint name collisions for overloaded methods
        var endpointNames = endpoint.Metadata.GetOrderedMetadata<IEndpointNameMetadata>();
        endpointNames.ShouldNotContain(x => x.EndpointName ==
            $"{typeof(OpenApiMetadataEndpoints).FullNameInCode()}.{nameof(OpenApiMetadataEndpoints.GetDefaultMetadata)}");
    }

    [Fact]
    public void endpoint_metadata_contains_summary()
    {
        var endpoint = EndpointFor("/openapi/with-summary");
        var summaryMeta = endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>();
        summaryMeta.ShouldNotBeNull();
        summaryMeta!.Summary.ShouldBe("Gets a greeting with summary");
    }

    [Fact]
    public void endpoint_metadata_contains_description()
    {
        var endpoint = EndpointFor("/openapi/with-summary");
        var descMeta = endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>();
        descMeta.ShouldNotBeNull();
        descMeta!.Description.ShouldBe(
            "This endpoint returns a simple greeting and demonstrates OpenAPI summary and description support");
    }

    #endregion

    #region API description metadata tests

    [Fact]
    public void api_description_action_descriptor_has_summary_metadata()
    {
        // Swashbuckle reads from WolverineActionDescriptor.EndpointMetadata
        var chain = HttpChains.ChainFor("GET", "/openapi/with-summary");
        var apiDesc = chain!.CreateApiDescription("GET");

        var summaries = apiDesc.ActionDescriptor.EndpointMetadata
            .OfType<IEndpointSummaryMetadata>()
            .ToList();

        summaries.ShouldNotBeEmpty();
        summaries.Last().Summary.ShouldBe("Gets a greeting with summary");
    }

    [Fact]
    public void api_description_action_descriptor_has_description_metadata()
    {
        var chain = HttpChains.ChainFor("GET", "/openapi/with-summary");
        var apiDesc = chain!.CreateApiDescription("GET");

        var descriptions = apiDesc.ActionDescriptor.EndpointMetadata
            .OfType<IEndpointDescriptionMetadata>()
            .ToList();

        descriptions.ShouldNotBeEmpty();
        descriptions.Last().Description.ShouldBe(
            "This endpoint returns a simple greeting and demonstrates OpenAPI summary and description support");
    }

    [Fact]
    public void api_description_provider_includes_summary_metadata()
    {
        var apiDescProvider = Host.Services
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>();

        var descs = apiDescProvider.ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .Where(d => d.RelativePath == "openapi/with-summary"
                        && d.ActionDescriptor is WolverineActionDescriptor)
            .ToList();

        descs.ShouldNotBeEmpty();

        var wolverineDesc = descs.First();
        wolverineDesc.ActionDescriptor.EndpointMetadata
            .OfType<IEndpointSummaryMetadata>()
            .Last()
            .Summary.ShouldBe("Gets a greeting with summary");
    }

    #endregion
}
