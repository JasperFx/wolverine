using Microsoft.OpenApi;
using Shouldly;

namespace WolverineWebApi.TestSupport;

public class ExpectStatusCodesAttribute : OpenApiExpectationAttribute
{
    public int[] StatusCodes { get; }

    public ExpectStatusCodesAttribute(params int[] statusCodes)
    {
        StatusCodes = statusCodes.OrderBy(x => x).ToArray();
    }

    public override void Validate(IOpenApiPathItem item, OpenApiOperation op, IOpenApiSource openApi)
    {
        var actual = op.Responses!.Keys.Select(int.Parse).OrderBy(x => x).ToArray();
        actual.ShouldBe(StatusCodes);
    }
}