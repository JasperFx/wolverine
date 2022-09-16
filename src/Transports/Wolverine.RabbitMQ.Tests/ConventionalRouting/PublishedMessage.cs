using Wolverine.Attributes;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting
{
    [MessageIdentity("published.message")]
    public class PublishedMessage
    {

    }
}
