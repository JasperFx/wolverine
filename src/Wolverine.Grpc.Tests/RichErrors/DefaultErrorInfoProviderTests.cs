using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests.RichErrors;

public class DefaultErrorInfoProviderTests
{
    private readonly DefaultErrorInfoProvider _provider = new();

    [Fact]
    public void emits_code_internal()
    {
        var status = _provider.BuildStatus(new InvalidOperationException("secret details"), context: null!);
        status.Code.ShouldBe((int)Code.Internal);
    }

    [Fact]
    public void packs_error_info_with_exception_type_name_as_reason()
    {
        var status = _provider.BuildStatus(new KeyNotFoundException("secret"), context: null!);

        var errorInfo = status.Details.Single().Unpack<ErrorInfo>();
        errorInfo.Reason.ShouldBe(nameof(KeyNotFoundException));
        errorInfo.Domain.ShouldBe(DefaultErrorInfoProvider.Domain);
    }

    [Fact]
    public void does_not_leak_exception_message_or_stack_trace()
    {
        var exception = new InvalidOperationException("connection string=Server=prod;Password=hunter2");

        var status = _provider.BuildStatus(exception, context: null!);

        status.Message.ShouldNotContain("hunter2");
        status.Message.ShouldNotContain("Password");

        var errorInfo = status.Details.Single().Unpack<ErrorInfo>();
        errorInfo.Metadata.ShouldBeEmpty();
    }
}
