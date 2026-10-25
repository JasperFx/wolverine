using System.Data.Common;
using Microsoft.Extensions.Logging;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class DeleteExpiredDeadLetterMessagesOperation : IDatabaseOperation
{
    private readonly IMessageDatabase _database;
    private readonly ILogger _logger;
    private readonly DateTimeOffset _utcNow;
    private readonly DurabilitySettings? _settings;

    public DeleteExpiredDeadLetterMessagesOperation(IMessageDatabase database, ILogger logger, DateTimeOffset utcNow)
    {
        _database = database;
        _logger = logger;
        _utcNow = utcNow;
    }

    /// <summary>
    /// GH-5209. Also expires archived dead letters and old replay history according to <paramref name="settings"/>
    /// </summary>
    public DeleteExpiredDeadLetterMessagesOperation(IMessageDatabase database, ILogger logger, DateTimeOffset utcNow,
        DurabilitySettings settings) : this(database, logger, utcNow)
    {
        _settings = settings;
    }

    public string Description { get; } = "Delete any expired dead letter messages from storage";
    public void ConfigureCommand(DbCommandBuilder builder)
    {
        DeadLetterExpiration.WriteStatements(builder, _database, _settings, _utcNow);
    }

    public Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        var count = reader.RecordsAffected;
        if (count > 0)
        {
            _logger.LogInformation("Deleted {Count} expired dead letter messages from database {Database}", count, _database.Name);
        }

        return Task.CompletedTask;
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        yield break;
    }
}