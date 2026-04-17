using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Shouldly;
using Xunit;

namespace Wolverine.Http.Grpc.Tests.RichErrors;

public class ValidationExceptionStatusDetailsProviderTests
{
    [Fact]
    public void returns_null_when_no_adapter_claims_exception()
    {
        var provider = new ValidationExceptionStatusDetailsProvider(Array.Empty<IValidationFailureAdapter>());

        var status = provider.BuildStatus(new InvalidOperationException(), context: null!);

        status.ShouldBeNull();
    }

    [Fact]
    public void uses_first_matching_adapter_and_stops()
    {
        var first = new StubAdapter(typeof(FooException), [("first.field", "first.msg")]);
        var second = new StubAdapter(typeof(FooException), [("second.field", "second.msg")]);

        var provider = new ValidationExceptionStatusDetailsProvider([first, second]);

        var status = provider.BuildStatus(new FooException(), context: null!);

        status.ShouldNotBeNull();
        status!.Code.ShouldBe((int)Code.InvalidArgument);
        var badRequest = status.Details.Single().Unpack<BadRequest>();
        badRequest.FieldViolations.Single().Field.ShouldBe("first.field");
    }

    [Fact]
    public void emits_one_field_violation_per_failure()
    {
        var adapter = new StubAdapter(typeof(FooException), [("a", "a-msg"), ("b", "b-msg"), ("c", "c-msg")]);
        var provider = new ValidationExceptionStatusDetailsProvider([adapter]);

        var status = provider.BuildStatus(new FooException(), context: null!);

        var badRequest = status!.Details.Single().Unpack<BadRequest>();
        badRequest.FieldViolations.Count.ShouldBe(3);
        badRequest.FieldViolations.Select(v => v.Field).ShouldBe(new[] { "a", "b", "c" });
    }

    private sealed class FooException : Exception;

    private sealed class StubAdapter(System.Type handled, IEnumerable<(string Field, string Description)> failures)
        : IValidationFailureAdapter
    {
        public bool CanHandle(Exception exception) => handled.IsInstanceOfType(exception);

        public IEnumerable<BadRequest.Types.FieldViolation> ToFieldViolations(Exception exception)
            => failures.Select(f => new BadRequest.Types.FieldViolation { Field = f.Field, Description = f.Description });
    }
}
