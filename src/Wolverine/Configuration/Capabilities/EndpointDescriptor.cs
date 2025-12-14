using JasperFx.Descriptors;

namespace Wolverine.Configuration.Capabilities;

public class EndpointDescriptor : OptionsDescription
{
    public EndpointDescriptor()
    {
    }

    public EndpointDescriptor(Endpoint endpoint) : base(endpoint)
    {
        Uri = endpoint.Uri;
    }
    
    public Uri Uri { get; set; }

    protected bool Equals(EndpointDescriptor other)
    {
        return Uri.Equals(other.Uri);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((EndpointDescriptor)obj);
    }

    public override int GetHashCode()
    {
        return Uri.GetHashCode();
    }
}