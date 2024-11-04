using Marten;
using Messages;
using Microsoft.Extensions.Logging;

namespace Orders;

public class RejectOrderHandler
{
    public async Task Handle(
        RejectOrder command,
        IDocumentSession session,
        ILogger logger
    )
    {
        var rejected = new OrderRejected(command.OrderId);
        session.Events.Append(command.OrderId, rejected);

        await session.SaveChangesAsync();
    }
}