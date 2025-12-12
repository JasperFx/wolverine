

using JasperFx.Descriptors;
using Wolverine.Persistence.Durability;

namespace Wolverine.Configuration.Capabilities;

public record MessageStore(
    Uri Uri,
    MessageStoreRole Role,
    DatabaseDescriptor Database)
{
    public static MessageStore For(IMessageStore store)
    {
        return new (store.Uri, store.Role, store.Describe());
    }
}