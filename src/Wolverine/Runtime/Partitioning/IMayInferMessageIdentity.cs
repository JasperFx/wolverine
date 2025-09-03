using System.Reflection;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.Partitioning;

public interface IMayInferMessageIdentity
{
    bool TryInferMessageIdentity(HandlerChain chain, out PropertyInfo property);
}