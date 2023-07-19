using Wolverine.Attributes;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.New;

[MessageIdentity("broadcasted")]
public class BroadcastedMessage
{
}