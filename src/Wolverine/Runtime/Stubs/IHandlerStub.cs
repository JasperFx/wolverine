namespace Wolverine.Runtime.Stubs;

public interface IHandlerStub<T>
{
    Task HandleAsync(T message, IMessageContext context, IServiceProvider services, CancellationToken cancellation);
}