using Wolverine;
using Wolverine.Attributes;

namespace PersistenceTests.SqlServer.Persistence;

public class SendItemEndpoint
{
    [Transactional]
    public ValueTask post_send_item(ItemCreated created, IMessageContext context)
    {
        return context.SendAsync(created);
    }
}