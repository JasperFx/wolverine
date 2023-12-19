using System.Diagnostics;
using Microsoft.OpenApi.Models;
using Shouldly;

namespace WolverineWebApi.TestSupport;

public class ExpectProducesAttribute : OpenApiExpectationAttribute
{
    public int StatusCode { get; }
    public Type ResponseType { get; }
    public string ContentType { get; }

    public ExpectProducesAttribute(int statusCode, Type responseType, string contentType)
    {
        StatusCode = statusCode;
        ResponseType = responseType;
        ContentType = contentType;
    }

    public override void Validate(OpenApiPathItem item, OpenApiOperation op, IOpenApiSource openApi)
    {
        op.Responses.Keys.ShouldContain(StatusCode.ToString());
        
        op.Responses.TryGetValue(StatusCode.ToString(), out var response).ShouldBeTrue();
        
        response.Content.Keys.ShouldContain(ContentType);

    }
}