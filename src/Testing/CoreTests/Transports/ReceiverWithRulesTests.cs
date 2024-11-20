using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

public class ReceiverWithRulesTests
{
    private readonly IReceiver theInner = Substitute.For<IReceiver>();
    private readonly IEnvelopeRule rule1 = Substitute.For<IEnvelopeRule>();
    private readonly IEnvelopeRule rule2 = Substitute.For<IEnvelopeRule>();
    private readonly ReceiverWithRules theReceiver;
    private readonly IListener theListener = Substitute.For<IListener>();

    public ReceiverWithRulesTests()
    {
        theReceiver = new ReceiverWithRules(theInner, [rule1, rule2]);
    }

    [Fact]
    public void dispose_delegates()
    {
        theReceiver.Dispose();
        theInner.Received().Dispose();
    }

    [Fact]
    public async Task receive_a_single_envelope()
    {
        var envelope = ObjectMother.Envelope();

        await theReceiver.ReceivedAsync(theListener, envelope);
        rule1.Received().Modify(envelope);
        rule2.Received().Modify(envelope);

        await theInner.Received().ReceivedAsync(theListener, envelope);
    }

    [Fact]
    public async Task receive_multiple_envelopes()
    {
        var envelope1 = ObjectMother.Envelope();
        var envelope2 = ObjectMother.Envelope();
        var envelope3 = ObjectMother.Envelope();

        var envelopes = new Envelope[] { envelope1, envelope2, envelope3 };

        await theReceiver.ReceivedAsync(theListener, envelopes);
        rule1.Received().Modify(envelope1);
        rule2.Received().Modify(envelope1);
        
        rule1.Received().Modify(envelope2);
        rule2.Received().Modify(envelope2);
        
        rule1.Received().Modify(envelope3);
        rule2.Received().Modify(envelope3);

        await theInner.Received().ReceivedAsync(theListener, envelopes);
    }
}