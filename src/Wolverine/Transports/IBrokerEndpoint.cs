using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wolverine.Transports;

public interface IBrokerEndpoint
{
    Uri Uri { get; }
    ValueTask<bool> CheckAsync();
    ValueTask TeardownAsync(ILogger logger);
    ValueTask SetupAsync(ILogger logger);
}