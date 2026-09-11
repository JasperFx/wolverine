using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Wolverine.Configuration;
using Wolverine.Configuration.EventModeling;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Acceptance.EventModel4395;

// GH-4395: after GH-4387 a message-handler slice whose message has no producer in the model -- it comes from
// another service, a hosted service, a controller -- carries no Pattern at all. "The code cannot say" and
// "nobody can say" were producing the same output. [SlicePattern] lets the handler say it, next to the code.
public class slice_pattern_attribute_4395
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    [Fact]
    public void a_declared_pattern_is_claimed_for_a_message_handler()
    {
        EventModelRoles.ForHandlerChain(chainFor<AssignTicketHandler>(x => AssignTicketHandler.Handle(null!)))
            .Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public void without_a_declaration_the_pattern_stays_unclaimed()
    {
        EventModelRoles.ForHandlerChain(chainFor<CloseTicketHandler>(x => CloseTicketHandler.Handle(null!)))
            .Pattern.ShouldBeNull();
    }

    [Fact]
    public void a_declaration_on_the_handler_type_applies_to_its_methods()
    {
        EventModelRoles.ForHandlerChain(chainFor<TicketAutomationHandler>(x => TicketAutomationHandler.Handle((TicketEscalated)null!)))
            .Pattern.ShouldBe(SlicePattern.Automation);
    }

    [Fact]
    public void a_declaration_on_the_method_wins_over_the_handler_type()
    {
        EventModelRoles.ForHandlerChain(chainFor<TicketAutomationHandler>(x => TicketAutomationHandler.Handle((TicketImported)null!)))
            .Pattern.ShouldBe(SlicePattern.Translation);
    }

    [Fact]
    public void the_quickstart_shape_now_colours_every_slice()
    {
        // OpenTicket arrives from outside the model and is declared; TicketOpened is cascaded by it and is
        // still promoted by the model-wide rule, exactly as before
        var open = EventModelRoles.ForHandlerChain(chainFor<OpenTicketHandler>(x => OpenTicketHandler.Handle(null!)));
        var react = EventModelRoles.ForHandlerChain(chainFor<TicketOpenedHandler>(x => TicketOpenedHandler.Handle(null!)));

        var model = WolverineEventModelSource.FinishModel(new EventModelDescriptor("app", new[] { open, react }));

        model.Slices.Single(x => x.Name == nameof(OpenTicket)).Pattern.ShouldBe(SlicePattern.Command);
        model.Slices.Single(x => x.Name == nameof(TicketOpened)).Pattern.ShouldBe(SlicePattern.Automation);
    }

    [Fact]
    public void a_declaration_is_not_promoted_by_the_cascade_inference()
    {
        // AssignTicket is cascaded by a sibling slice, which would make it an Automation -- but it is also sent
        // from outside the model, and the handler says so. The declaration answers the question the rule guesses at.
        var escalate = EventModelRoles.ForHandlerChain(chainFor<EscalateTicketHandler>(x => EscalateTicketHandler.Handle(null!)));
        escalate.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(AssignTicket) });

        var assign = EventModelRoles.ForHandlerChain(chainFor<AssignTicketHandler>(x => AssignTicketHandler.Handle(null!)));

        var model = WolverineEventModelSource.FinishModel(new EventModelDescriptor("app", new[] { escalate, assign }));

        model.Slices.Single(x => x.Name == nameof(AssignTicket)).Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public void a_declaration_does_not_override_a_schedule()
    {
        EventModelRoles.ForHandlerChain(chainFor<TicketReminderHandler>(x => TicketReminderHandler.Handle(null!)))
            .Pattern.ShouldBe(SlicePattern.Automation);
    }

    [Fact]
    public void a_declaration_does_not_override_an_http_route()
    {
        var chain = chainFor<TicketAutomationHandler>(x => TicketAutomationHandler.Handle((TicketEscalated)null!));

        var query = EventModelRoles.Describe(chain,
            new EventModelSliceSeed("GET /tickets", TriggerKind.Http, null, null, typeof(TicketAutomationHandler))
            {
                IsQuery = true
            });
        query.Pattern.ShouldBe(SlicePattern.View);

        var post = EventModelRoles.Describe(chain,
            new EventModelSliceSeed("POST /tickets", TriggerKind.Http, null, null, typeof(TicketAutomationHandler)));
        post.Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public void a_declaration_does_not_override_a_grpc_rpc()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<TicketAutomationHandler>(x => TicketAutomationHandler.Handle((TicketEscalated)null!)));
        slice.Pattern.ShouldBe(SlicePattern.Automation);

        var manifest = new StubGrpcManifest(new GrpcEndpointDescriptor("Tickets", "Escalate",
            typeof(TicketEscalated), null, typeof(TicketAutomationHandler),
            GrpcServiceDiscoveryMode.CodeFirst, GrpcRpcStreamKind.Unary));

        var applied = WolverineEventModelSource.ApplyGrpcTriggers(new EventModelDescriptor("app", new[] { slice }), manifest);

        applied.Slices.Single().Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public void a_stale_declaration_disagrees_with_the_board_instead_of_going_unseen()
    {
        var derived = EventModelRoles.ForHandlerChain(chainFor<AssignTicketHandler>(x => AssignTicketHandler.Handle(null!)))
            .WithProvenance(EventModelProvenance.Derived);

        var board = (EventModelSliceDescriptor.Named(derived.Name) with
        {
            Pattern = SlicePattern.Automation
        }).WithProvenance(EventModelProvenance.Declared);

        var merged = board.Merge(derived);

        merged.ProvenanceFor(EventModelRole.Pattern).ShouldBe(EventModelProvenance.Derived);
        merged.Hotspots.ShouldContain(x => x.Origin == HotspotOrigin.SourceDisagreement);
    }

    private sealed class StubGrpcManifest(params GrpcEndpointDescriptor[] endpoints) : IGrpcEndpointManifest
    {
        public IReadOnlyList<GrpcEndpointDescriptor> Endpoints { get; } = endpoints;
    }
}

#region sample types for GH-4395

public record OpenTicket(string Title);

public record TicketOpened(Guid Id);

public record AssignTicket(Guid Id, string Assignee);

public record EscalateTicket(Guid Id);

public record CloseTicket(Guid Id);

public record TicketEscalated(Guid Id);

public record TicketImported(Guid Id);

public record TicketReminderDue(Guid Id) : TimeoutMessage(TimeSpan.FromHours(1));

public class OpenTicketHandler
{
    [SlicePattern(SlicePattern.Command)]
    public static TicketOpened Handle(OpenTicket command) => new(Guid.NewGuid());
}

public class TicketOpenedHandler
{
    public static void Handle(TicketOpened opened)
    {
    }
}

public class AssignTicketHandler
{
    [SlicePattern(SlicePattern.Command)]
    public static void Handle(AssignTicket command)
    {
    }
}

public class EscalateTicketHandler
{
    public static AssignTicket Handle(EscalateTicket command) => new(command.Id, "on-call");
}

public class CloseTicketHandler
{
    public static void Handle(CloseTicket command)
    {
    }
}

[SlicePattern(SlicePattern.Automation)]
public class TicketAutomationHandler
{
    public static void Handle(TicketEscalated escalated)
    {
    }

    [SlicePattern(SlicePattern.Translation)]
    public static void Handle(TicketImported imported)
    {
    }
}

public class TicketReminderHandler
{
    [SlicePattern(SlicePattern.Command)]
    public static void Handle(TicketReminderDue due)
    {
    }
}

#endregion
