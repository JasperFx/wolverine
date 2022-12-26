namespace Wolverine.Persistence.Durability;

internal interface IMessagingAction
{
    string Description { get; }
    Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent);
}