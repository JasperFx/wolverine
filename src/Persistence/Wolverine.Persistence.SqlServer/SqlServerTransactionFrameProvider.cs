using Wolverine.Configuration;
using Wolverine.Persistence.Database;
using Lamar;
using Microsoft.Data.SqlClient;

namespace Wolverine.Persistence.SqlServer;

internal class SqlServerTransactionFrameProvider : ITransactionFrameProvider
{
    public void ApplyTransactionSupport(IChain chain, IContainer container)
    {
        var shouldFlushOutgoingMessages = chain.ShouldFlushOutgoingMessages();


        var frame = new DbTransactionFrame<SqlTransaction, SqlConnection>
            { ShouldFlushOutgoingMessages = shouldFlushOutgoingMessages };

        chain.Middleware.Add(frame);
    }
}
