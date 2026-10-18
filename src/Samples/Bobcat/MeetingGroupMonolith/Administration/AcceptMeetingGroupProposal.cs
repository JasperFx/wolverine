using Marten;
using MeetingGroupMonolith;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Administration;

public static class AcceptMeetingGroupProposalEndpoint
{
    // Cascade: accepting a proposal publishes MeetingGroupProposalAcceptedEvent
    // so the Meetings module creates the actual MeetingGroup
    [WolverinePost("/api/administration/proposals/{id}/accept")]
    public static (MeetingGroupProposal, MeetingGroupProposalAcceptedEvent) Post(
        Guid id,
        [Entity(Required = true)] MeetingGroupProposal proposal,
        IDocumentSession session)
    {
        proposal.Status = ProposalStatus.Accepted;
        proposal.DecisionDate = DateTimeOffset.UtcNow;
        session.Store(proposal);

        return (proposal, new MeetingGroupProposalAcceptedEvent(
            proposal.Id, proposal.Name, proposal.Description,
            proposal.LocationCity, proposal.LocationCountryCode, proposal.ProposalUserId));
    }
}
