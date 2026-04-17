using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using ProtoMessage = Google.Protobuf.IMessage;

namespace Wolverine.Http.Grpc.Tests.RichErrors;

public class InlineStatusDetailsProviderTests
{
    [Fact]
    public void returns_null_when_exception_type_does_not_match()
    {
        var config = new GrpcRichErrorDetailsConfiguration()
            .MapException<TargetException>(StatusCode.FailedPrecondition, (_, _) => Array.Empty<ProtoMessage>());

        var provider = BuildFirstProvider(config);
        var status = provider.BuildStatus(new InvalidOperationException(), context: null!);

        status.ShouldBeNull();
    }

    [Fact]
    public void emits_configured_status_code_and_packs_supplied_details()
    {
        var config = new GrpcRichErrorDetailsConfiguration()
            .MapException<TargetException>(StatusCode.FailedPrecondition,
                (_, _) => new ProtoMessage[] { new PreconditionFailure { Violations = { new PreconditionFailure.Types.Violation { Type = "policy", Subject = "s", Description = "d" } } } });

        var provider = BuildFirstProvider(config);
        var status = provider.BuildStatus(new TargetException("boom"), context: null!);

        status.ShouldNotBeNull();
        status!.Code.ShouldBe((int)StatusCode.FailedPrecondition);
        status.Message.ShouldBe("boom");
        status.Details.Single().Unpack<PreconditionFailure>().Violations.Single().Type.ShouldBe("policy");
    }

    private static IGrpcStatusDetailsProvider BuildFirstProvider(GrpcRichErrorDetailsConfiguration config)
    {
        var services = new ServiceCollection();
        foreach (var registration in config.Registrations)
        {
            registration(services);
        }

        var sp = services.BuildServiceProvider();
        return sp.GetServices<IGrpcStatusDetailsProvider>().Single();
    }

    private sealed class TargetException(string message) : Exception(message);
}
