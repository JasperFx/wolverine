using Microsoft.Extensions.Logging;
using Wolverine.Configuration;

namespace Wolverine.Transports;

public interface IBrokerEndpoint
{
    Uri Uri { get; }
    ValueTask<bool> CheckAsync();
    ValueTask TeardownAsync(ILogger logger);
    ValueTask SetupAsync(ILogger logger);
    
    /// <summary>
    ///     Is the endpoint controlled and configured by the application or Wolverine itself?
    /// </summary>
    public EndpointRole Role { get; }
}