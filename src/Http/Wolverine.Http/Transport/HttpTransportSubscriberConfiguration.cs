using Wolverine.Configuration;

namespace Wolverine.Http.Transport;

public class HttpTransportSubscriberConfiguration : SubscriberConfiguration<HttpTransportSubscriberConfiguration, HttpEndpoint>
{
    internal HttpTransportSubscriberConfiguration(HttpEndpoint endpoint) : base(endpoint)
    {
    }
}