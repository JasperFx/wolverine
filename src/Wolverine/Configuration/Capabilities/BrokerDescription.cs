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
        Endpoint = subject.DescribeEndpoint();
    }

    public string ProtocolName { get; set; } = null!;
    public string Name { get; set; } = null!;
    public Uri? ReplyUri { get; set; }

    /// <summary>
    /// A sanitized, credential-free summary of what this broker is pointing to (host + port, virtual host, namespace,
    /// region, bootstrap servers, …) for monitoring consoles. Null when the transport reports no connection target.
    /// Built from parsed connection components only — never contains usernames, passwords, SAS keys, or connection
    /// strings. See <see cref="ITransport.DescribeEndpoint"/>.
    /// </summary>
    public string? Endpoint { get; set; }
}