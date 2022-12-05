namespace Quickstart;

#region sample_Quickstart_IssueRepository

public class IssueRepository
{
    private readonly Dictionary<Guid, Issue> _issues = new();

    public void Store(Issue issue)
    {
        _issues[issue.Id] = issue;
    }

    public Issue Get(Guid id)
    {
        if (_issues.TryGetValue(id, out var issue))
        {
            return issue;
        }

        throw new ArgumentOutOfRangeException(nameof(id), "Issue does not exist");
    }
}

#endregion