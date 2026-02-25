namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

public record ScheduledMessageCount(string ServiceName, string MessageType, Uri Database, int Count);
