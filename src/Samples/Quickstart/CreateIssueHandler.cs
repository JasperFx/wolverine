#region sample_Quickstart_CreateIssueHandler

namespace Quickstart;

public class CreateIssueHandler
{
    private readonly IssueRepository _repository;

    public CreateIssueHandler(IssueRepository repository)
    {
        _repository = repository;
    }

    // The IssueCreated event message being returned will be
    // published as a new "cascaded" message by Wolverine after
    // the original message and any related middleware has
    // succeeded
    public IssueCreated Handle(CreateIssue command)
    {
        var issue = new Issue
        {
            Title = command.Title,
            Description = command.Description,
            IsOpen = true,
            Opened = DateTimeOffset.Now,
            OriginatorId = command.OriginatorId
        };

        _repository.Store(issue);

        return new IssueCreated(issue.Id);
    }
}

#endregion