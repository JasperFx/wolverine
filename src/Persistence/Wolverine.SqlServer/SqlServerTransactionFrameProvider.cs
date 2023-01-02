using System.Linq;
using Lamar;
using Microsoft.Data.SqlClient;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.RDBMS;

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

    public bool CanApply(IChain chain, IContainer container)
    {
        return chain.ServiceDependencies(container).Any(x => x == typeof(SqlConnection));
    }
}