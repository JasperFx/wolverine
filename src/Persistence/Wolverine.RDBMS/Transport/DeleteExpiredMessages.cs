using System.Data.Common;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Transport;

internal class DeleteExpiredMessages : IDatabaseOperation, IDoNotReturnData
{
    private readonly DatabaseControlTransport _transport;
    private readonly DateTimeOffset _utcNow;

    public DeleteExpiredMessages(DatabaseControlTransport transport, DateTimeOffset utcNow)
    {
        _transport = transport;
        _utcNow = utcNow;
    }


    public string Description => "Delete expired control queue messages";
    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append($"delete from {_transport.TableName} where expires < ");
        builder.AppendParameter(_utcNow);
        builder.Append(";");
    }

    public Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        yield break;
    }
}