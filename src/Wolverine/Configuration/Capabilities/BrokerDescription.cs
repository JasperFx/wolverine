using JasperFx.Descriptors;
using Wolverine.Transports;

namespace Wolverine.Configuration.Capabilities;

public class BrokerDescription : OptionsDescription
{
    public BrokerDescription()
    {
    }

    public BrokerDescription(ITransport subject) : base(subject)
    {
        ProtocolName = subject.Protocol;
        Name = subject.Name;

        ReplyUri = subject.ReplyEndpoint()?.Uri;
    }

    public string ProtocolName { get; set; } = null!;
    public string Name { get; set; } = null!;
    public Uri? ReplyUri { get; set; }
}