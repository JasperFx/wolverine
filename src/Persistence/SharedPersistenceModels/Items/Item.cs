namespace SharedPersistenceModels.Items;

public class Item
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    
    public bool Approved { get; set; }
}