using System.Threading;
using System.Threading.Tasks;

namespace Wolverine.Runtime.Handlers;

#region sample_MessageHandler

public interface IMessageHandler
{
    Task HandleAsync(IMessageContext context, CancellationToken cancellation);
}

public abstract class MessageHandler : IMessageHandler
{
    public HandlerChain? Chain { get; set; }

    public abstract Task HandleAsync(IMessageContext context, CancellationToken cancellation);
}

#endregion
