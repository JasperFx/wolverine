using Wolverine.Attributes;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Existing;

[MessageIdentity("routed")]
public class RoutedMessage
{
}