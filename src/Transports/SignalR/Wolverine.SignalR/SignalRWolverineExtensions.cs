using JasperFx.Core.Reflection;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR;

public static class SignalRWolverineExtensions
{
    ///     Quick access to the Rabbit MQ Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static SignalRTransport SignalRTransport(this WolverineOptions endpoints, BrokerName? name = null)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<SignalRTransport>(name);
    }
}