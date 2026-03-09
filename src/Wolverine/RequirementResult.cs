namespace Wolverine;

/// <summary>
/// Used in Wolverine to denote the correctness of a data requirement or other validation rule
/// </summary>
/// <param name="Continue"></param>
/// <param name="Messages"></param>
public record RequirementResult(HandlerContinuation Branch, string[] Messages);