using Microsoft.Extensions.Hosting;

namespace Wolverine.Runtime.Agents;

public interface IAgent : IHostedService
{
    Uri Uri { get; }
}