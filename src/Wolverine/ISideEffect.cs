namespace Wolverine;

/// <summary>
/// Marker interface for a return value from a Wolverine
/// handler action. Any *public* Execute() or ExecuteAsync() method will be
/// called on this object
/// </summary>
public interface ISideEffect
{
    
}