using System.Data.Common;
using System.Threading.Tasks;

namespace Wolverine.RDBMS;

public static class DbOutboxExtensions
{
    public static Task EnlistInOutboxAsync(this IMessageContext context, DbTransaction tx)
    {
        var transaction = new DatabaseEnvelopeOutbox(context, tx);
        return context.EnlistInOutboxAsync(transaction);
    }
}
