using Wolverine.Attributes;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

[MessageIdentity("published.message")]
public class PublishedMessage;