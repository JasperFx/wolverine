using Wolverine.Attributes;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

[MessageIdentity("routed")]
public class RoutedMessage
{
}

[MessageIdentity("routed2")]
public class Routed2Message
{
}