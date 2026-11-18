using EventSourcedSagas.Fulfillment;
using JasperFx.Events;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace EventSourcedSagas.Specs;

// GH-5415: a start that loses the race to start its saga's stream is reported as a SagaConcurrencyException whichever
// store refused it. Marten 10, Polecat 6 and Fisher 2 all throw JasperFx.Events' ExistingStreamIdCollisionException, and
// a store may report the race as a plain concurrency failure instead, so both are checked. The race itself is covered
// on all three stores by "a start that loses the race to another start is refused as any saga start is".
public class translating_a_lost_start_race
{
    public static TheoryData<string, Exception> Collisions => new()
    {
        { "Marten, Polecat and Fisher", new ExistingStreamIdCollisionException(Guid.NewGuid(), typeof(OrderFulfillment)) },
        { "an optimistic concurrency failure", new JasperFx.ConcurrencyException("the stream moved on") }
    };

    [Theory]
    [MemberData(nameof(Collisions))]
    public void a_lost_start_race_is_recognised(string store, Exception collision)
    {
        EventSourcedSagaSupport.IsStreamCollision(collision).ShouldBeTrue(store);

        var translated = EventSourcedSagaSupport.StreamCollision(typeof(OrderFulfillment), Guid.NewGuid(), collision);
        translated.ShouldBeOfType<SagaConcurrencyException>().InnerException.ShouldBeSameAs(collision);
    }

    [Fact]
    public void a_saga_concurrency_exception_is_not_translated_twice()
        => EventSourcedSagaSupport.IsStreamCollision(new SagaConcurrencyException("already translated")).ShouldBeFalse();

    [Fact]
    public void any_other_failure_is_left_alone()
        => EventSourcedSagaSupport.IsStreamCollision(new InvalidOperationException()).ShouldBeFalse();
}
