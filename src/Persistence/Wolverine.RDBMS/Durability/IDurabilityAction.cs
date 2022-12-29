using System.Threading.Tasks;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Durability;

internal interface IDurabilityAction
{
    string Description { get; }
    Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent, NodeSettings nodeSettings,
        DatabaseSettings databaseSettings);
}