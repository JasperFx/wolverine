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

        // GH-4531: naming the store that cannot do it is only half the answer; name the ones that can.
        throw new InvalidOperationException(
            $"The message store ({context.Storage}) for this application does not implement {typeof(ISagaSupport).FullNameInCode()}, so it cannot store saga state for {typeof(TSaga).FullNameInCode()}. " +
            $"Saga state is stored by the message store: PersistMessagesWithPostgresql/SqlServer/MySql/Sqlite/Oracle (lightweight saga tables), " +
            $"IntegrateWithWolverine() on a Marten/Polecat/Fisher store, an EF Core DbContext with a DbSet<{typeof(TSaga).NameInCode()}> under UseEntityFrameworkCoreTransactions(), " +
            $"or RavenDb/CosmosDb/Redis persistence. Register one that supports sagas.");
    }
}