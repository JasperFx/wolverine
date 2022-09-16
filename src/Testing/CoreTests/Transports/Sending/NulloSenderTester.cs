using System.Threading.Tasks;
using CoreTests.Messaging;
using Wolverine.Transports.Sending;
using Wolverine.Util;
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
