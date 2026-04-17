using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Shouldly;
using Xunit;

namespace Wolverine.Http.Grpc.Tests.RichErrors;

[Collection("grpc-rich-errors")]
public class rich_error_details_code_first_tests : IClassFixture<RichErrorsCodeFirstFixture>
{
    private readonly RichErrorsCodeFirstFixture _fixture;

    public rich_error_details_code_first_tests(RichErrorsCodeFirstFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task validation_failure_round_trips_as_bad_request_via_trailer()
    {
        var client = _fixture.CreateClient();

        var ex = await Should.ThrowAsync<RpcException>(
            () => client.Greet(new GreetCommand { Name = "", Age = -1 }));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);

        var richStatus = ex.GetRpcStatus();
        richStatus.ShouldNotBeNull();
        richStatus!.Code.ShouldBe((int)Code.InvalidArgument);

        var badRequestAny = richStatus.Details.Single(d => d.Is(BadRequest.Descriptor));
        var badRequest = badRequestAny.Unpack<BadRequest>();
        badRequest.FieldViolations.Count.ShouldBe(2);
        badRequest.FieldViolations.ShouldContain(v => v.Field == "Name" && v.Description == "Name is required");
        badRequest.FieldViolations.ShouldContain(v => v.Field == "Age" && v.Description == "Age must be positive");
    }

    [Fact]
    public async Task valid_request_passes_through_without_any_rich_status()
    {
        var client = _fixture.CreateClient();

        var reply = await client.Greet(new GreetCommand { Name = "Erik", Age = 30 });

        reply.Message.ShouldBe("Hello, Erik");
    }
}
