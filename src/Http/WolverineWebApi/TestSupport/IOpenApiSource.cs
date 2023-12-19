using Microsoft.OpenApi.Models;

namespace WolverineWebApi.TestSupport;

public interface IOpenApiSource
{
    (OpenApiPathItem, OpenApiOperation) FindOpenApiDocument(OperationType httpMethod, string path);
}