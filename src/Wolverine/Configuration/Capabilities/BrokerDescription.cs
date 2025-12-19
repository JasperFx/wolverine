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

    public string ProtocolName { get; set; }
    public string Name { get; set; }
    public Uri? ReplyUri { get; set; }
}