namespace MartenAndRabbitMessages;

public record CreateIssue(Guid OriginatorId, string Title, string Description);