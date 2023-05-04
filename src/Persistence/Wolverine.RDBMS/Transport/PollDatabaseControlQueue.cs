using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Transport;

internal class PollDatabaseControlQueue : IDatabaseOperation, IAgentCommand
{
    private readonly DatabaseControlTransport _transport;
    private readonly IReceiver _receiver;
    private readonly DatabaseControlListener _listener;
    private readonly List<Envelope> _envelopes = new();

    public PollDatabaseControlQueue(DatabaseControlTransport transport, IReceiver receiver,
        DatabaseControlListener listener)
    {
        _transport = transport;
        _receiver = receiver;
        _listener = listener;
    }

    public string Description { get; } = "Polling for new control messages";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append($"select body from {_transport.TableName} where node_id = ");
        builder.AppendParameter(_transport.Options.UniqueNodeId);
        builder.Append(";");
    }

    public async Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        while (await reader.ReadAsync(token))
        {
            var body = await reader.GetFieldValueAsync<byte[]>(0, token);
            var envelope = EnvelopeSerializer.Deserialize(body);
            _envelopes.Add(envelope);
        }
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        if (_envelopes.Any())
        {
            yield return this;
        }
    }

    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _receiver.ReceivedAsync(_listener, _envelopes.ToArray());

        await _transport.DeleteEnvelopesAsync(_envelopes, cancellationToken);
        
        yield break;
    }

}