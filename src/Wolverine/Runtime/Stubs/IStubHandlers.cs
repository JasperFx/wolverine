namespace Wolverine.Runtime.Stubs;

public interface IStubHandlers
{
    bool HasAny();
    void Stub<T>(Func<T, IMessageContext, IServiceProvider, CancellationToken, Task> func);
    void Stub<TRequest, TResponse>(Func<TRequest, TResponse> func);
    void Clear<T>();
    void ClearAll();
}