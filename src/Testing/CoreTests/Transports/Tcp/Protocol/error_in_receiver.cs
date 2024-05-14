using Xunit;

namespace CoreTests.Transports.Tcp.Protocol;

public class error_in_receiver : ProtocolContext
{
    public error_in_receiver()
    {
        theReceiver.ThrowErrorOnReceived = true;
    }

    [Fact]
    public async Task did_not_succeed()
    {
        await afterSending();
        theSender.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task logs_processing_failure_in_sender()
    {
        await afterSending();
        theSender.ProcessingFailed.ShouldBeTrue();
    }
}