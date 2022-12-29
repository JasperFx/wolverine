using System.Threading.Tasks;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Durability;

internal interface IMessagingAction
{
    string Description { get; }
    Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent);
}