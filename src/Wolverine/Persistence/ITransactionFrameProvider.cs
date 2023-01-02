using Lamar;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

public interface ITransactionFrameProvider
{
    void ApplyTransactionSupport(IChain chain, IContainer container);
    bool CanApply(IChain chain, IContainer container);
}