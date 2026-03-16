using NSubstitute;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;
using Wolverine.Util;
using Xunit;

namespace CoreTests.ErrorHandling;

public class MoveToErrorQueueTests
{
    private readonly IEnvelopeLifecycle _lifecycle;
    private readonly IWolverineRuntime _runtime;
    private readonly Envelope _envelope;

    public MoveToErrorQueueTests()
    {
        _lifecycle = Substitute.For<IEnvelopeLifecycle>();
        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(new WolverineOptions());
        _runtime.MessageTracking.Returns(Substitute.For<IMessageTracker>());

        _envelope = new Envelope
        {
            Destination = new Uri("local://queue")
        };
        _lifecycle.Envelope.Returns(_envelope);
    }

    [Fact]
    public async Task should_assign_fallback_MessageType_when_Message_and_MessageType_are_null()
    {
        _envelope.Message = null;
        _envelope.MessageType = null;

        var exception = new UnknownMessageTypeNameException("test");
        var continuation = new MoveToErrorQueue(exception);

        await continuation.ExecuteAsync(_lifecycle, _runtime, DateTimeOffset.UtcNow, null);

        _envelope.MessageType.ShouldBe("unknown/UnknownMessageTypeNameException");
    }

    [Fact]
    public async Task should_preserve_existing_MessageType_when_Message_is_null()
    {
        _envelope.Message = null;
        _envelope.MessageType = "com.example.orders.placed.v1";

        var exception = new UnknownMessageTypeNameException("test");
        var continuation = new MoveToErrorQueue(exception);

        await continuation.ExecuteAsync(_lifecycle, _runtime, DateTimeOffset.UtcNow, null);

        _envelope.MessageType.ShouldBe("com.example.orders.placed.v1");
    }

    [Fact]
    public async Task should_use_dotnet_type_when_Message_is_present()
    {
        _envelope.Message = new SampleMessage("test");
        _envelope.MessageType = "some.old.value";

        var exception = new InvalidOperationException("test");
        var continuation = new MoveToErrorQueue(exception);

        await continuation.ExecuteAsync(_lifecycle, _runtime, DateTimeOffset.UtcNow, null);

        _envelope.MessageType.ShouldBe(typeof(SampleMessage).ToMessageTypeName());
    }
}

public record SampleMessage(string Name);
