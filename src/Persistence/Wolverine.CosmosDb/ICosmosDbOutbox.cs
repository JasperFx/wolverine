using Microsoft.Azure.Cosmos;

namespace Wolverine.CosmosDb;

/// <summary>
///     Outbox-ed messaging sending with CosmosDb
/// </summary>
public interface ICosmosDbOutbox : IMessageBus
{
    /// <summary>
    ///     Current CosmosDB container
    /// </summary>
    Container Container { get; }

    /// <summary>
    ///     Enroll a CosmosDb container into the outbox'd sender
    /// </summary>
    /// <param name="container"></param>
    void Enroll(Container container);
}
