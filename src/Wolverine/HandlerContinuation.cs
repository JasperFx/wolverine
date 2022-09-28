namespace Wolverine;

public enum HandlerContinuation
{
    /// <summary>
    /// When used in middleware, directs Wolverine to continue processing an incoming command
    /// </summary>
    Continue,
    
    /// <summary>
    /// When used in middleware, directs Wolverine to stop processing an incoming command
    /// </summary>
    Stop
}