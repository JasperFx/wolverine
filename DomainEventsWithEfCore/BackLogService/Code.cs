namespace BackLogService;

public class BacklogItem
{
    public Guid Id { get; private set; }

    public string Description { get; private set; }
    public virtual Sprint Sprint { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    
    public void CommitTo(Sprint sprint)
    {
        Sprint = sprint;
        // TODO -- do something to publish a domain event
    }
}

public record BackLotItemCommitted(Guid ItemId, Guid SprintId);

public class Sprint
{
    public Guid Id { get; set; }
}