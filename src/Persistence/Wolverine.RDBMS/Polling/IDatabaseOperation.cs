using System.Data.Common;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Polling;

public interface IDatabaseOperation
{
    string Description { get; }

    // Assume these things are stateful
    void ConfigureCommand(DbCommandBuilder builder);

    Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token);

    IEnumerable<IAgentCommand> PostProcessingCommands();
}