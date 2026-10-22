using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// E1/R2 (durable compliance rig). The eager idempotency check puts a Handled inbox row into the handler's own
/// transaction and stamps the envelope as persisted. Both stand only if that transaction commits: a failed attempt
/// rolls the row back, and an envelope left stamped skipped the check on its retry, so no record of completion
/// survived it.
/// </summary>
public class eager_idempotency_stamp_is_undone_on_rollback
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly IEnvelopeTransaction theTransaction = Substitute.For<IEnvelopeTransaction>();
    private readonly MessageContext theContext;

    public eager_idempotency_stamp_is_undone_on_rollback()
    {
        theEnvelope.Status = EnvelopeStatus.Incoming;
        theEnvelope.WasPersistedInInbox = false;

        theTransaction.TryMakeEagerIdempotencyCheckAsync(theEnvelope, Arg.Any<DurabilitySettings>(),
            Arg.Any<CancellationToken>()).Returns(true);

        theContext = new MessageContext(theRuntime);
        theContext.ReadEnvelope(theEnvelope, Substitute.For<IChannelCallback>());
        theContext.EnlistInOutbox(theTransaction);
    }

    [Fact]
    public async Task a_failed_attempt_puts_the_envelope_back_the_way_the_check_found_it()
    {
        await theContext.AssertEagerIdempotencyAsync(CancellationToken.None);
        theEnvelope.WasPersistedInInbox.ShouldBeTrue();

        // What the transaction may have stamped on the way through
        theEnvelope.Status = EnvelopeStatus.Handled;

        // The executor's failure path
        await theContext.ClearAllAsync();

        theEnvelope.WasPersistedInInbox.ShouldBeFalse();
        theEnvelope.Status.ShouldBe(EnvelopeStatus.Incoming);
    }

    [Fact]
    public async Task a_retry_makes_the_check_again()
    {
        await theContext.AssertEagerIdempotencyAsync(CancellationToken.None);
        await theContext.ClearAllAsync();

        await theContext.EnlistInOutboxAsync(theTransaction);
        await theContext.AssertEagerIdempotencyAsync(CancellationToken.None);

        await theTransaction.Received(2).TryMakeEagerIdempotencyCheckAsync(theEnvelope,
            Arg.Any<DurabilitySettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task an_envelope_the_receiver_persisted_is_left_alone()
    {
        // A durable endpoint's row really is persisted, and the check never stamped anything
        theEnvelope.WasPersistedInInbox = true;

        await theContext.AssertEagerIdempotencyAsync(CancellationToken.None);
        await theContext.ClearAllAsync();

        theEnvelope.WasPersistedInInbox.ShouldBeTrue();
    }
}
