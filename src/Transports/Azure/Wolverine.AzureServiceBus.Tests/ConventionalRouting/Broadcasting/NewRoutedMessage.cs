using Wolverine.Attributes;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Broadcasting;

[MessageIdentity("newrouted")]
public class NewRoutedMessage
{
}