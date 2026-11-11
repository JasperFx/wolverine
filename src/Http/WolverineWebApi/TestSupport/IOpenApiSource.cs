using Microsoft.OpenApi;

namespace WolverineWebApi.TestSupport;

public interface IOpenApiSource
{
    (IOpenApiPathItem, OpenApiOperation) FindOpenApiDocument(OperationType httpMethod, string path);

    /// <summary>
    /// The full generated OpenAPI document, used by request-body expectations to resolve
    /// <c>$ref</c> schemas against <see cref="OpenApiDocument.Components"/>.
    /// </summary>
    OpenApiDocument GetOpenApiDocument();
}
