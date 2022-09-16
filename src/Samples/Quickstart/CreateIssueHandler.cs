#region sample_Quickstart_CreateIssueHandler

namespace Quickstart;

public class CreateIssueHandler
{
    private readonly IssueRepository _repository;

    public CreateIssueHandler(IssueRepository repository)
    {
        _repository = repository;
    }

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
