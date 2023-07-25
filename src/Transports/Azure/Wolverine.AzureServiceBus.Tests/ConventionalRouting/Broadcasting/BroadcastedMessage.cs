using Wolverine.Attributes;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Broadcasting;

[MessageIdentity("broadcasted")]
public class BroadcastedMessage
{
}