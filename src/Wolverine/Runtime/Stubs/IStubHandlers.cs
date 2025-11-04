namespace Wolverine.Runtime.Stubs;

public interface IStubHandlers
{
    /// <summary>
    /// Are there any registered stub message handlers in this Wolverine application?
    /// </summary>
    /// <returns></returns>
    bool HasAny();
    
    /// <summary>
    /// Apply a complex stubbed message handling behavior for a message type T that
    /// is normally handled by an external system for testing scenarios
    /// </summary>
    /// <param name="func"></param>
    /// <typeparam name="T"></typeparam>
    void Stub<T>(Func<T, IMessageContext, IServiceProvider, CancellationToken, Task> func);
    
    /// <summary>
    /// Apply a simple stubbed behavior for request/reply scenarios for a message type
    /// TRequest that normally results in a TResponse from an external system
    /// </summary>
    /// <param name="func"></param>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    void Stub<TRequest, TResponse>(Func<TRequest, TResponse> func);
    
    /// <summary>
    /// Clear any previously registered stub behavior for the message type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    void Clear<T>();
    
    /// <summary>
    /// Clear any registered message stub behavior in the application
    /// across all message types
    /// </summary>
    void ClearAll();
}