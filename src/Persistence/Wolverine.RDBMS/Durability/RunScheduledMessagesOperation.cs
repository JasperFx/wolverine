using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class RunScheduledMessagesOperation : IDatabaseOperation, IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly List<Envelope> _envelopes = new();
    private readonly ILocalQueue _localQueue;
    private readonly DurabilitySettings _settings;

    public RunScheduledMessagesOperation(IMessageDatabase database, DurabilitySettings settings, ILocalQueue localQueue)
    {
        _database = database;
        _settings = settings;
        _localQueue = localQueue;
    }

    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _database.ReassignIncomingAsync(_settings.AssignedNodeNumber, _envelopes);

        foreach (var envelope in _envelopes)
        {
            runtime.Logger.LogInformation("Locally enqueuing scheduled message {Id} of type {MessageType}", envelope.Id,
                envelope.MessageType);
            _localQueue.Enqueue(envelope);
        }

        yield break;
    }

    public string Description { get; } = "Run Scheduled Messages";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        _database.WriteLoadScheduledEnvelopeSql(builder, DateTimeOffset.UtcNow);
    }

    public async Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        while (await reader.ReadAsync(token))
        {
            var envelope = await DatabasePersistence.ReadIncomingAsync(reader, token);
            _envelopes.Add(envelope);
        }
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        if (!_envelopes.Any())
        {
            yield break;
        }

        yield return this;
    }
}