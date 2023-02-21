using Wolverine.Configuration;

namespace Wolverine.Runtime.Routing;

public interface ILocalMessageRoutingConvention
{
    /// <summary>
    ///     Override the type to local queue naming. By default this is the MessageTypeName
    ///     to lower case invariant
    /// </summary>
    /// <param name="determineName"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    ILocalMessageRoutingConvention Named(Func<Type, string> determineName);

    /// <summary>
    ///     Customize the endpoints
    /// </summary>
    /// <param name="customization"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    ILocalMessageRoutingConvention CustomizeQueues(Action<Type, IListenerConfiguration> customization);
}