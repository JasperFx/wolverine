namespace WolverineWebApi.TestSupport;

/// <summary>
/// The HTTP method of an OpenAPI operation, for use in attribute arguments. Microsoft.OpenApi 2.x keys
/// <c>OpenApiPathItem.Operations</c> by <see cref="System.Net.Http.HttpMethod"/>, which is not a
/// compile-time constant and so cannot be an attribute argument; this stands in for the 1.x
/// <c>OperationType</c> enum the expectations were written against.
/// </summary>
public enum OperationType
{
    Get,
    Put,
    Post,
    Delete,
    Options,
    Head,
    Patch,
    Trace
}

public static class OperationTypeExtensions
{
    public static HttpMethod ToHttpMethod(this OperationType operationType)
    {
        return new HttpMethod(operationType.ToString().ToUpperInvariant());
    }
}
