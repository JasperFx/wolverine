namespace ChaosTesting;

public class MessageRecord
{
    public static MessageRecord For(ITrackedMessage message)
    {
        return new MessageRecord
        {
            Id = message.Id,
            TypeName = message.GetType().Name
        };
    }

    public Guid Id { get; set; }
    public string TypeName { get; set; }
}