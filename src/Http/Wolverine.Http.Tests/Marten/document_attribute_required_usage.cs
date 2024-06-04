using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace Wolverine.Http.Tests.Marten;

public class document_attribute_required_usage(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public async Task separate_attributes_call_load_method()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/document-required/separate-attributes/" + Guid.NewGuid());
            x.StatusCodeShouldBe(404);
        });

        var problemDetails = await result.ReadAsJsonAsync<ProblemDetails>();
        problemDetails.ShouldNotBeNull();
        problemDetails.Title.ShouldBe("Invoice is not found");
        problemDetails.Detail.ShouldBe("We only get here with [Document][Required]");
    }
    
    [Fact]
    public async Task document_attribute_only_does_not_call_load_method()
    {
        // This call gets short-circuited by DocumentAttribute.Required and thus the DocumentRequiredEndpoint.Load method is not called
        var result = await Scenario(x =>
        {
            x.Get.Url("/document-required/document-attribute-only/" + Guid.NewGuid());
            x.StatusCodeShouldBe(404);
        });

        var problemDetails = await result.ReadAsTextAsync();
        problemDetails.ShouldBeEmpty();
    }
}