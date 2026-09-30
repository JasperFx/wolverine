using System.Data.Common;
using Microsoft.Extensions.Logging;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Transport;

internal class PollDatabaseControlQueue : IDatabaseOperation, IAgentCommand
{
    private readonly List<Envelope> _envelopes = new();
    private readonly List<UnreadableControlRow> _unreadable = new();
    private readonly DatabaseControlListener _listener;
    private readonly IReceiver _receiver;
    private readonly DatabaseControlTransport _transport;

    public PollDatabaseControlQueue(DatabaseControlTransport transport, IReceiver receiver,
        DatabaseControlListener listener)
    {
        _transport = transport;
        _receiver = receiver;
        _listener = listener;
    }

    /// <summary>
    ///     GH-4718: a control queue row this node cannot turn back into an Envelope. Held by id so the row
    ///     can be removed, and with the sender's own <c>message_type</c> so the log can name what it was.
    /// </summary>
    private record UnreadableControlRow(Guid Id, string MessageType, int BodyLength, Exception Exception);

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        // GH-4321: only runs when the poll actually found envelopes — keep the listener at its
        // active polling cadence while traffic is flowing
        _listener.MarkActivity();

        if (_unreadable.Count != 0)
        {
            foreach (var row in _unreadable)
            {
                // GH-4718: the operator's only clue used to be "Failed to process message IAgentCommand from
                // local://agents/" with an InvalidEnvelopeException, which names neither the row, the sender,
                // nor the message — the reporter could not find the offending envelope in any table. Every
                // identifying value is right here in the row we just read.
                runtime.Logger.LogError(row.Exception,
                    "Discarding an unreadable control queue message {ControlMessageId} of type {MessageType} ({BodyLength} bytes) addressed to node {NodeId}. The message cannot be processed and has been deleted so that the control queue keeps draining; if this is an agent command, the leader will re-evaluate and reissue it. A body larger than Options.MaxIncomingEnvelopeDataSize ({MaxDataSize} bytes) is the usual cause.",
                    row.Id, row.MessageType, row.BodyLength, _transport.Options.UniqueNodeId,
                    EnvelopeSerializer.Limits.MaxDataSize);
            }

            await _transport.DeleteRowsAsync(_unreadable.Select(x => x.Id).ToList(), cancellationToken);
        }

        if (_envelopes.Count != 0)
        {
            await _receiver.ReceivedAsync(_listener, _envelopes.ToArray());

            await _transport.DeleteEnvelopesAsync(_envelopes, cancellationToken);
        }

        return AgentCommands.Empty;
    }

    public string Description => "Polling for new control messages";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append($"select id, message_type, body from {_transport.TableName} where node_id = ");
        builder.AppendParameter(_transport.Options.UniqueNodeId);
        builder.Append(';');
    }

    public async Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        while (await reader.ReadAsync(token))
        {
            var id = await reader.GetFieldValueAsync<Guid>(0, token);
            var messageType = await reader.GetFieldValueAsync<string>(1, token);
            var body = await reader.GetFieldValueAsync<byte[]>(2, token);

            try
            {
                _envelopes.Add(EnvelopeSerializer.Deserialize(body));
            }
            catch (Exception e)
            {
                // GH-4718: one unreadable row used to abort the whole poll, and because DeleteExpiredMessages
                // shares this operation's transaction, the abort rolled back the very expiry that would have
                // cleared the row. The node then re-read it every poll and received NO control message at all
                // for as long as it sat there — the leader's "confirmed 0 of N agents" stall. Quarantine the
                // one row and let the rest of the batch through.
                _unreadable.Add(new UnreadableControlRow(id, messageType, body.Length, e));
            }
        }
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        if (_envelopes.Count != 0 || _unreadable.Count != 0)
        {
            yield return this;
        }
    }
}
