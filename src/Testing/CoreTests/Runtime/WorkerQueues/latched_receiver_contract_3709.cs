using JasperFx.Core;
using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-3709. <c>ListeningAgent.LatchReceiver()</c> used to be an if/else chain naming
/// <c>DurableReceiver</c>, <c>BufferedReceiver</c> and <c>InlineReceiver</c> one at a time. When GH-3708
/// added <see cref="NativeAckReceiver" /> the chain silently skipped it, and the consequence was not a
/// compile error or an exception but a stop-and-drain that stopped waiting: an unlatched receiver's
/// <c>DrainAsync</c> returns immediately, so the transport channel closed underneath running handlers, every
/// unsettled delivery went back to the broker, and on an exclusive listener handoff the incoming node re-ran
/// those messages concurrently with the outgoing one -- precisely the intra-group concurrency that
/// partitioned processing exists to prevent.
/// </summary>
public class latched_receiver_contract_3709
{
    /// <summary>
    /// A receiver that knows how to latch has to say so through <c>ILatchedReceiver</c>, because that is the
    /// only thing <c>LatchReceiver()</c> looks at now. A <c>Latch()</c> method that no caller can see is the
    /// exact shape of the original bug.
    /// </summary>
    [Fact]
    public void every_receiver_that_can_latch_declares_ILatchedReceiver()
    {
        var offenders = typeof(IReceiver).Assembly
            .GetTypes()
            .Where(x => x is { IsClass: true, IsAbstract: false } && x.CanBeCastTo<IReceiver>())
            .Where(x => x.GetMethod("Latch", Type.EmptyTypes) != null)
            .Where(x => !x.CanBeCastTo<ILatchedReceiver>())
            .Select(x => x.FullNameInCode())
            .ToArray();

        offenders.ShouldBeEmpty(
            "These receivers have a Latch() method that ListeningAgent.LatchReceiver() cannot see: "
            + offenders.Join(", "));
    }

    /// <summary>
    /// And the four that exist today are all wired up, so the guard above is asserting against a non-empty
    /// population rather than passing vacuously.
    /// </summary>
    [Theory]
    [InlineData(typeof(DurableReceiver))]
    [InlineData(typeof(BufferedReceiver))]
    [InlineData(typeof(InlineReceiver))]
    [InlineData(typeof(NativeAckReceiver))]
    public void the_known_receivers_are_latchable(Type receiverType)
    {
        receiverType.CanBeCastTo<ILatchedReceiver>().ShouldBeTrue();
    }
}
