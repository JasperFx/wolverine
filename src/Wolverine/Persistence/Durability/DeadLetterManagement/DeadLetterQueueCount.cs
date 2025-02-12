namespace Wolverine.Persistence.Durability.DeadLetterManagement;

/// <summary>
/// Summarized count of dead letter messages
/// </summary>
/// <param name="ServiceName"></param>
/// <param name="ReceivedAt"></param>
/// <param name="MessageType"></param>
/// <param name="ExceptionType"></param>
/// <param name="TenantId"></param>
/// <param name="Count"></param>
public record DeadLetterQueueCount(string ServiceName, Uri ReceivedAt, string MessageType, string ExceptionType, Uri Database, int Count);