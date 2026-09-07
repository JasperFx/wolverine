using JasperFx.Descriptors;
using Wolverine.Configuration;
using Wolverine.Persistence.EventSourcing;
using JasperFx.Events.EventModeling;
using Wolverine.Configuration.EventModeling;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Acceptance.EventModel4387;

// GH-4387: an inbound message that appends events is a Command slice or an Automation slice depending
// on WHY it arrived -- a person asked for it, or the system reacted to something -- and a handler
// signature cannot tell the two apart. Claiming Command unconditionally put the Derived rung on a role
// only a declaration can fill, where per-role precedence made the guess beat the board and minted a
// SourceDisagreement per automation for the privilege. Same reasoning GH-4181 applied to TriggerLabel.
public class unclaimed_pattern_for_message_handlers_4387
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    [Fact]
    public void a_message_handler_leaves_the_pattern_unclaimed()
    {
        EventModelRoles.ForHandlerChain(chainFor<ProposeHomeCheckAppointmentHandler>(
            x => ProposeHomeCheckAppointmentHandler.Handle(null!))).Pattern.ShouldBeNull();
    }

    [Fact]
    public void a_declared_automation_survives_the_merge_instead_of_disagreeing_with_the_code()
    {
        var derived = EventModelRoles.ForHandlerChain(chainFor<ProposeHomeCheckAppointmentHandler>(
            x => ProposeHomeCheckAppointmentHandler.Handle(null!))).WithProvenance(EventModelProvenance.Derived);

        var board = (EventModelSliceDescriptor.Named(derived.Name) with
        {
            Pattern = SlicePattern.Automation
        }).WithProvenance(EventModelProvenance.Declared);

        var merged = board.Merge(derived);

        merged.Pattern.ShouldBe(SlicePattern.Automation);
        merged.ProvenanceFor(EventModelRole.Pattern).ShouldBe(EventModelProvenance.Declared);
        merged.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.SourceDisagreement);
    }

    [Fact]
    public void the_role_is_still_claimed_where_the_model_actually_answers_it()
    {
        // an event another slice emits is evidence, not a guess -- the derived rung claims Automation
        var propose = EventModelRoles.ForHandlerChain(chainFor<ProposeHomeCheckAppointmentHandler>(
            x => ProposeHomeCheckAppointmentHandler.Handle(null!)));

        var react = EventModelSliceDescriptor.Named("react") with
        {
            CommandType = TypeDescriptor.For(typeof(HomeCheckAppointmentProposed))
        };

        var model = WolverineEventModelSource.FinishModel(
            new EventModelDescriptor("app", new[] { propose, react }));

        model.Slices.Single(x => x.Name == "react").Pattern.ShouldBe(SlicePattern.Automation);
    }

    [Fact]
    public void a_scheduled_slice_is_an_automation_because_its_trigger_says_so()
    {
        // "events *or a schedule* -> processor -> command / message", in SlicePattern's own words. The
        // trigger kind answers the question the signature could not, so the role is claimed.
        EventModelRoles.ForHandlerChain(chainFor<SweepStaleAppointmentsHandler>(
            x => SweepStaleAppointmentsHandler.Handle(null!))).Pattern.ShouldBe(SlicePattern.Automation);
    }

    [Fact]
    public void a_grpc_rpc_is_an_inbound_request_somebody_made_so_it_is_a_command()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<ProposeHomeCheckAppointmentHandler>(
            x => ProposeHomeCheckAppointmentHandler.Handle(null!)));

        var manifest = new StubGrpcManifest(new GrpcEndpointDescriptor("Appointments", "Propose",
            typeof(HomeCheckAssignmentAccepted), null, typeof(ProposeHomeCheckAppointmentHandler),
            GrpcServiceDiscoveryMode.CodeFirst, GrpcRpcStreamKind.Unary));

        var applied = WolverineEventModelSource.ApplyGrpcTriggers(
            new EventModelDescriptor("app", new[] { slice }), manifest);

        applied.Slices.Single().Pattern.ShouldBe(SlicePattern.Command);
    }

    private sealed class StubGrpcManifest(params GrpcEndpointDescriptor[] endpoints) : IGrpcEndpointManifest
    {
        public IReadOnlyList<GrpcEndpointDescriptor> Endpoints { get; } = endpoints;
    }
}

#region sample types for GH-4387

public record HomeCheckAssignmentAccepted(Guid Id);

public record HomeCheckAppointmentProposed(Guid Id);

public record AppointmentSweepDue(Guid Id) : TimeoutMessage(TimeSpan.FromHours(1));

public class ProposeHomeCheckAppointmentHandler
{
    [Emits(typeof(HomeCheckAppointmentProposed))]
    public static void Handle(HomeCheckAssignmentAccepted trigger)
    {
    }
}

public class SweepStaleAppointmentsHandler
{
    public static void Handle(AppointmentSweepDue due)
    {
    }
}

#endregion
