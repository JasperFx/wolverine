using Wolverine.Attributes;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Existing;

[MessageIdentity("published.message")]
public class PublishedMessage
{
}