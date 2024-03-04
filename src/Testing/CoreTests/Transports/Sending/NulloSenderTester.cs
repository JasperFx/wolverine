using JasperFx.Core;
using TestingSupport;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports.Sending;

public class NulloSenderTester
{
    [Fact]
    public async Task enqueue_automatically_marks_envelope_as_successful()
    {
        var sender = new NullSender("tcp://localhost:3333".ToUri());

        var env = ObjectMother.Envelope();

        await sender.SendAsync(env);
    }
}