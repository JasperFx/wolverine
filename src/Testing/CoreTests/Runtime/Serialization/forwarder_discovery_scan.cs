using CoreTests.Transports.Tcp;
using Shouldly;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Runtime.Serialization;

// GH-2909: Forwarders.FindForwards now routes its scan through JasperFx's central TypeQuery instead
// of an ad-hoc Assembly.ExportedTypes walk. This pins that it still discovers IForwardsTo<>
// implementations (OriginalMessage : IForwardsTo<NewMessage> lives in this test assembly).
public class forwarder_discovery_scan
{
    [Fact]
    public void find_forwards_still_discovers_IForwardsTo_implementations()
    {
        var forwarders = new Forwarders();
        forwarders.FindForwards(typeof(OriginalMessage).Assembly);

        forwarders.Relationships.ContainsKey(typeof(OriginalMessage)).ShouldBeTrue();
        forwarders.Relationships[typeof(OriginalMessage)].ShouldBe(typeof(NewMessage));
    }
}
