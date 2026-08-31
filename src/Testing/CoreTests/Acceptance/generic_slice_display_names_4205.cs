using Shouldly;
using Wolverine.Configuration.EventModeling;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Acceptance.EventModel4205;

/// <summary>
/// GH-4205. A handler for a closed generic message minted a slice literally labelled
/// <c>IEvent`1</c> — the CLR spelling of <see cref="System.Type.Name" />. Unreadable on the canvas,
/// and identical for every closed form of the same open generic, so two relays of different payloads
/// collided on one slice name. Slice names are the merge key across model sources (GH-3988), so
/// every source that names a slice has to answer this the same way.
/// </summary>
public class generic_slice_display_names_4205
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    [Fact]
    public void a_closed_generic_message_reads_as_source_would_spell_it()
    {
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<ClaimReleasedRelayHandler>(x => ClaimReleasedRelayHandler.Handle(null!)));

        slice.Name.ShouldBe("Relay<ClaimReleased>");
    }

    /// <summary>
    /// The point of the previous test, stated the other way: two closed forms of one open generic are
    /// two slices, and under Type.Name they were one.
    /// </summary>
    [Fact]
    public void two_closed_forms_of_the_same_generic_are_two_slices()
    {
        var released = EventModelRoles.ForHandlerChain(
            chainFor<ClaimReleasedRelayHandler>(x => ClaimReleasedRelayHandler.Handle(null!)));
        var claimed = EventModelRoles.ForHandlerChain(
            chainFor<ClaimTakenRelayHandler>(x => ClaimTakenRelayHandler.Handle(null!)));

        released.Name.ShouldNotBe(claimed.Name);
        claimed.Name.ShouldBe("Relay<ClaimTaken>");
    }

    /// <summary>
    /// A slice name is also a merge key, and ShortNameInCode() prefixes a nested type with its
    /// declaring type — so this stays on Type.Name for everything that is not generic, rather than
    /// renaming every existing slice whose message happens to be a nested class.
    /// </summary>
    [Fact]
    public void a_non_generic_message_is_named_exactly_as_before()
    {
        EventModelRoles.DisplayNameFor(typeof(ClaimReleased)).ShouldBe("ClaimReleased");
        EventModelRoles.DisplayNameFor(typeof(NestedMessages.Inner)).ShouldBe("Inner");
    }
}

public record ClaimReleased;

public record ClaimTaken;

public record Relay<T>(T Body);

public static class NestedMessages
{
    public record Inner;
}

public class ClaimReleasedRelayHandler
{
    public static void Handle(Relay<ClaimReleased> message)
    {
    }
}

public class ClaimTakenRelayHandler
{
    public static void Handle(Relay<ClaimTaken> message)
    {
    }
}
