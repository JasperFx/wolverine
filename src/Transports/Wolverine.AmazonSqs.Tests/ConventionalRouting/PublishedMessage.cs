using Wolverine.Attributes;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

[MessageIdentity("published.message")]
public class PublishedMessage
{
}