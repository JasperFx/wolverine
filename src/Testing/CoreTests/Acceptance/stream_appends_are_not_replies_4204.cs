using JasperFx.Events;
using Shouldly;
using Wolverine.Configuration.EventModeling;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Acceptance.EventModel4204;

/// <summary>
/// GH-4204. A handler holding an <see cref="IEventStream{T}"/> appends imperatively INTO the stream, so
/// what it RETURNS is a reply or a cascaded message. The classifier only knew the chain was "event
/// sourced" and read every return value as an emitted event, so a slice reported its reply DTO as one of
/// its events — and, because imperative appends are invisible to a static read, reported nothing else.
/// </summary>
/// <remarks>
/// The half this does NOT fix, by design and as the issue says: the events actually appended through the
/// stream stay invisible. <see cref="EventModelRoles"/> reads declarative returns only — that limit is
/// stated on the class and is what CritterWatch's Roslyn source generator exists for. Reporting nothing
/// is honest; reporting the reply DTO was not.
/// </remarks>
public class stream_appends_are_not_replies_4204
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    [Fact]
    public void a_reply_dto_from_a_stream_appending_handler_is_not_an_emitted_event()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<ClaimHandler>(x => ClaimHandler.Handle(null!, null!)));

        slice.EmittedEvents.ShouldBeEmpty();
        slice.PublishedMessages.Select(x => x.Name).ShouldContain(nameof(ClaimResult));
    }

    /// <summary>
    /// The stream still establishes the write model — this is not achieved by forgetting the chain is
    /// event sourced.
    /// </summary>
    [Fact]
    public void the_stream_still_names_the_write_model()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<ClaimHandler>(x => ClaimHandler.Handle(null!, null!)));

        slice.AggregateTypes.Select(x => x.Name).ShouldContain(nameof(NodeClaim));
    }

    /// <summary>
    /// The opposite case keeps the old reading: a chain returning an untyped event collection cannot have
    /// its element types read off the collection, so its other return values ARE the events. Regression
    /// guard for the condition this fix added.
    /// </summary>
    [Fact]
    public void a_returned_event_collection_still_makes_the_other_returns_events()
    {
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<StreamAndCollectionHandler>(x => StreamAndCollectionHandler.Handle(null!, null!)));

        slice.EmittedEvents.Select(x => x.Name).ShouldContain(nameof(NodeClaimed));
    }

    /// <summary>
    /// And a declarative aggregate handler — no stream, events returned directly — is untouched. This is
    /// the mainstream Critter Stack shape and the one that must not regress.
    /// </summary>
    [Fact]
    public void a_declarative_write_model_handler_still_reports_its_returned_event()
    {
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<DeclarativeClaimHandler>(x => DeclarativeClaimHandler.Handle(null!, null!)));

        slice.EmittedEvents.Select(x => x.Name).ShouldContain(nameof(NodeClaimed));
        slice.PublishedMessages.ShouldBeEmpty();
    }
}

public record ClaimNode(string NodeId);

public record NodeClaimed(string NodeId);

public record ClaimResult(string NodeId, bool Conflict);

public class NodeClaim
{
    public string Id { get; set; } = null!;
}

public class ClaimHandler
{
    // The shape from the issue: appends through the stream, returns the InvokeAsync<T> reply
    public static ClaimResult Handle(ClaimNode command, [WriteModel(Required = false)] IEventStream<NodeClaim> stream)
    {
        stream.AppendOne(new NodeClaimed(command.NodeId));
        return new ClaimResult(command.NodeId, false);
    }
}

public class StreamAndCollectionHandler
{
    public static (IEnumerable<object>, NodeClaimed) Handle(ClaimNode command,
        [WriteModel(Required = false)] IEventStream<NodeClaim> stream)
    {
        return ([], new NodeClaimed(command.NodeId));
    }
}

public class DeclarativeClaimHandler
{
    public static NodeClaimed Handle(ClaimNode command, [WriteModel] NodeClaim claim)
    {
        return new NodeClaimed(command.NodeId);
    }
}
