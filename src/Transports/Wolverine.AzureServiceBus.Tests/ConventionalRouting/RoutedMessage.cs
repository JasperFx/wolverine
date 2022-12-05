using Wolverine.Attributes;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

[MessageIdentity("routed")]
public class RoutedMessage
{
}