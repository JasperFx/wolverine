using FluentValidation;
using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Administration;

public record ProposeNewMeetingGroup(string Name, string Description, string LocationCity, string LocationCountryCode, Guid ProposalUserId)
{
    public class Validator : AbstractValidator<ProposeNewMeetingGroup>
    {
        public Validator()
        {
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.LocationCity).NotEmpty();
            RuleFor(x => x.LocationCountryCode).NotEmpty().Length(2);
        }
    }
}

/// <summary>
/// 201 Created with a Location header pointing at the new proposal. Wolverine.HTTP's
/// <see cref="CreationResponse"/> is the idiomatic way to say 201 from an endpoint, and unlike
/// an <c>IResult</c> it can sit in the first slot of a cascading-message tuple.
/// </summary>
public record ProposalCreation(Guid Id) : CreationResponse($"/api/administration/proposals/{Id}");

public static class ProposeNewMeetingGroupEndpoint
{
    [WolverinePost("/api/administration/proposals")]
    public static ProposalCreation Post(ProposeNewMeetingGroup command, IDocumentSession session)
    {
        var proposal = new MeetingGroupProposal
        {
            Id = Guid.NewGuid(),
            Name = command.Name,
            Description = command.Description,
            LocationCity = command.LocationCity,
            LocationCountryCode = command.LocationCountryCode,
            ProposalUserId = command.ProposalUserId,
            ProposalDate = DateTimeOffset.UtcNow,
        };

        session.Store(proposal);
        return new ProposalCreation(proposal.Id);
    }
}

public static class GetMeetingGroupProposalEndpoint
{
    [WolverineGet("/api/administration/proposals/{id}")]
    public static MeetingGroupProposal? Get(Guid id, [Entity] MeetingGroupProposal? proposal) => proposal;
}
