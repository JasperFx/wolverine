using System.Reflection;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

public static class SagaSupport<TId, TSaga> where TSaga : Saga
{
    public static ValueTask<ISagaStorage<TId, TSaga>> EnrollAndFetchSagaStorage(MessageContext context)
    {
        if (context.Storage is ISagaSupport sagaSupport)
        {
            return sagaSupport.EnrollAndFetchSagaStorage<TId, TSaga>(context);
        }

        throw new InvalidOperationException(
            $"The message store ({context.Storage}) for this application does not implement {typeof(ISagaSupport).FullNameInCode()}");
    }
}