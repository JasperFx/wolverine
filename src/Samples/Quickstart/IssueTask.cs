namespace Quickstart;

public class IssueTask
{
    public IssueTask(string title, string description)
    {
        Title = title;
        Description = description;
        Id = Guid.NewGuid();
    }

    public Guid Id { get; set; }

    public string Title { get; set; }
    public string Description { get; set; }
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset Finished { get; set; }
}