using System.ComponentModel.DataAnnotations.Schema;

namespace SharedPersistenceModels.Items;

public class Item : Entity
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    
    public bool Approved { get; set; }
    
    [NotMapped]
    public bool AutoApproveInInterceptor { get; set; }

    public void Approve()
    {
        Approved = true;
        Publish(new ItemApproved(Id));
    }
}

public record ItemApproved(Guid Id) : IDomainEvent;

public interface IDomainEvent;

public interface IEntity
{
    IReadOnlyList<IDomainEvent> Events { get; }
}

public abstract class Entity : IEntity
{
    private IReadOnlyList<IDomainEvent> _events;
    public List<object> Events { get; } = new();

    IReadOnlyList<IDomainEvent> IEntity.Events => Events.OfType<IDomainEvent>().ToList();

    public void Publish(IDomainEvent e)
    {
        Events.Add(e);
    }
}