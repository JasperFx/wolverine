using Wolverine.Runtime.RemoteInvocation;
using Xunit;

namespace CoreTests.Runtime.RemoteInvocation;

public class AcknowledgementTests
{
    [Fact]
    public void bi_directionally_serialize_Acknowledgement()
    {
        var ack = new Acknowledgement
        {
            RequestId = Guid.NewGuid(),
            Timestamp = DateTime.Today
        };

        var bytes = ack.Write();

        var ack2 = (Acknowledgement)Acknowledgement.Read(bytes);
        
        ack2.RequestId.ShouldBe(ack.RequestId);
        ack2.Timestamp.ShouldBe(ack.Timestamp);
    }
}