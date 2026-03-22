namespace MartenAndRabbitMessages;

public class Issue
{
    public Guid Id { get; } = Guid.NewGuid();

    public Guid? AssigneeId { get; set; }
    public Guid? OriginatorId { get; set; }
    public string Title { get; set; } = null!;
    public string Description { get; set; } = null!;
    public bool IsOpen { get; set; }

    public DateTimeOffset Opened { get; set; }

    public IList<IssueTask> Tasks { get; set; }
        = new List<IssueTask>();
}