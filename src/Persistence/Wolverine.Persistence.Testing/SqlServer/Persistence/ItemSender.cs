using System.Threading.Tasks;
using Wolverine.Attributes;

namespace Wolverine.Persistence.Testing.SqlServer.Persistence;

public class SendItemEndpoint
{
    [Transactional]
    public ValueTask post_send_item(ItemCreated created, IMessageContext context)
    {
        return context.SendAsync(created);
    }
}
