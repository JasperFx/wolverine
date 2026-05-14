using System.Diagnostics.CodeAnalysis;
using JasperFx.Descriptors;
using Wolverine.Transports;

namespace Wolverine.Configuration.Capabilities;

public class BrokerDescription : OptionsDescription
{
    public BrokerDescription()
    {
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "OptionsDescription(subject) reads subject.GetType().GetProperties() to build a diagnostic description. BrokerDescription is a diagnostic surface (Capabilities reporting); properties of subject's runtime type that are trimmed are silently omitted, which is acceptable for this purpose.")]
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