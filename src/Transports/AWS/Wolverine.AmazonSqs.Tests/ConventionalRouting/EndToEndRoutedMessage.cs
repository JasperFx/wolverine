using Wolverine.Attributes;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

/// <summary>
/// end_to_end_with_conventional_routing needs a queue nothing else in this namespace listens to.
///
/// The CI shard runs this project across three worker PROCESSES partitioned by test class, so
/// CollectionPerAssembly only buys serialization inside one process. Every other conventional
/// routing class here stands up a host listening at <c>sqs://routed</c> for
/// <see cref="RoutedMessage"/>; a concurrent worker holding that listener receives the end-to-end
/// message and the tracked session times out waiting for a delivery that already happened
/// somewhere else. Its own message type gives it its own queue. See GH-3763.
/// </summary>
[MessageIdentity("end-to-end-routed")]
public class EndToEndRoutedMessage;

public class EndToEndRoutedMessageHandler
{
    public void Handle(EndToEndRoutedMessage message)
    {
    }
}
