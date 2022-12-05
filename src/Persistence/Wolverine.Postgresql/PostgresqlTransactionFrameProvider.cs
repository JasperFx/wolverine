using Lamar;
using Npgsql;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql;

internal class PostgresqlTransactionFrameProvider : ITransactionFrameProvider
{
    public void ApplyTransactionSupport(IChain chain, IContainer container)
    {
        var shouldFlushOutgoingMessages = chain.ShouldFlushOutgoingMessages();


        var frame = new DbTransactionFrame<NpgsqlTransaction, NpgsqlConnection>
            { ShouldFlushOutgoingMessages = shouldFlushOutgoingMessages };

        chain.Middleware.Add(frame);
    }
}