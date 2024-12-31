using Wolverine.Persistence;

namespace CoreTests.Persistence;

public class StorageSamples
{
    
}

public class Item
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

public interface IProfanityDetector
{
    bool HasProfanity(string text);
}

#region sample_using_conditional_storage_action

public record CreateItem(Guid Id, string Name);

public static class CreateItemHandler
{
    // It's always a struggle coming up with sample use cases
    public static IStorageAction<Item> Handle(
        CreateItem command, 
        IProfanityDetector detector)
    {
        // First see if the name is valid
        if (detector.HasProfanity(command.Name))
        {
            // and if not, do nothing
            return Storage.Nothing<Item>();
        }

        return Storage.Insert(new Item
        {
            Id = command.Id, 
            Name = command.Name
        });
    }
}

#endregion

