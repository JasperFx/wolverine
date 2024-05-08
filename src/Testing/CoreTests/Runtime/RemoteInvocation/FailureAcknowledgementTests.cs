using Wolverine.Runtime.RemoteInvocation;
using Xunit;

namespace CoreTests.Runtime.RemoteInvocation;

public class FailureAcknowledgementTests
{
    [Fact]
    public void serialize_FailureAcknowledgement()
    {
        var ack = new FailureAcknowledgement
        {
            Message = "Bad",
            RequestId = Guid.NewGuid()
        };

        var bytes = ack.Write();

        var ack2 = (FailureAcknowledgement)FailureAcknowledgement.Read(bytes);

        ack2.Message.ShouldBe(ack.Message);
        ack2.RequestId.ShouldBe(ack.RequestId);

    }
}