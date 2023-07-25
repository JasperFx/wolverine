using Wolverine.Attributes;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Broadcasting;

[MessageIdentity("newpublished.message")]
public class NewPublishedMessage
{
}