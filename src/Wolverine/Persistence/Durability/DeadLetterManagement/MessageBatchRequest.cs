namespace Wolverine.Persistence.Durability.DeadLetterManagement;

public record MessageBatchRequest(Guid[] Ids, Uri? Database = null);