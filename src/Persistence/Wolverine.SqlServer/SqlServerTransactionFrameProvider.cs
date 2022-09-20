using Wolverine.Configuration;
using Wolverine.RDBMS;
using Lamar;
using Microsoft.Data.SqlClient;
using Wolverine.Persistence;

namespace Wolverine.SqlServer;

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
