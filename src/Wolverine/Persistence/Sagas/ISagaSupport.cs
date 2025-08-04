using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

public interface ISagaSupport
{
    ValueTask<ISagaStorage<TId, TSaga>> EnrollAndFetchSagaStorage<TId, TSaga>(MessageContext context) where TSaga : Saga;
}