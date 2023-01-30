namespace ItemService;

public class CreateItemWithDbContextNotIntegratedWithOutboxCommand
{
    public string Name { get; set; }
}

public class ItemCreatedInDbContextNotIntegratedWithOutbox
{
    public Guid Id { get; set; }
}

public class CreateItemWithDbContextNotIntegratedWithOutboxCommandHandler
{
    public static ItemCreatedInDbContextNotIntegratedWithOutbox Handle(
        // This would be the message
        CreateItemWithDbContextNotIntegratedWithOutboxCommand command,

        // Any other arguments are assumed
        // to be service dependencies
        ItemsDbContextWithoutOutbox db)
    {
        // Create a new Item entity
        var item = new Item
        {
            Name = command.Name
        };

        // Add the item to the current
        // DbContext unit of work
        db.Items.Add(item);

        // This event being returned
        // by the handler will be automatically sent
        // out as a "cascading" message
        return new ItemCreatedInDbContextNotIntegratedWithOutbox
        {
            Id = item.Id
        };
    }
}

public class ItemCreatedInDbContextNotIntegratedWithOutboxHandler
{
    public void Handle(ItemCreatedInDbContextNotIntegratedWithOutbox @event)
    {
        Console.WriteLine("You created a new item with id " + @event.Id);
    }
}