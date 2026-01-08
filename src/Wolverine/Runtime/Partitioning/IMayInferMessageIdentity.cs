using System.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.Partitioning;

public interface IMayInferMessageIdentity
{
    bool TryInferMessageIdentity(IChain chain, out PropertyInfo property);
}