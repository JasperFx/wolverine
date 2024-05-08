using Microsoft.OpenApi.Models;
using Shouldly;

namespace WolverineWebApi.TestSupport;

public class ExpectMatchAttribute : OpenApiExpectationAttribute
{
    public OperationType OperationType { get; }
    public string Path { get; }

    public ExpectMatchAttribute(OperationType operationType, string path)
    {
        OperationType = operationType;
        Path = path;
    }

    public override void Validate(OpenApiPathItem item, OpenApiOperation op, IOpenApiSource openApi)
    {
        var (_, expected) = openApi.FindOpenApiDocument(OperationType, Path);

        op.Responses.Keys.OrderBy(x => x).ToArray().ShouldBe(expected.Responses.Keys.OrderBy(x => x).ToArray(), "Expected status codes");

        foreach (var responsesKey in expected.Responses.Keys)
        {
            var expectedResponse = expected.Responses[responsesKey];
            var actualResponse = op.Responses[responsesKey];

            actualResponse.Content.Keys.ToArray().ShouldBe(expectedResponse.Content.Keys.ToArray(), "Expected content for response " + responsesKey);
        }
    }
}