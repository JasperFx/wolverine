using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Sagas;

public class SagaStorageVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type.Closes(typeof(ISagaStorage<,>)) || type.Closes(typeof(ISagaStorage<>));
    }

    public Variable Create(Type type)
    {
        var sagaType = type.GetGenericArguments().Last();
        var idType = SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType();

        return typeof(EnrollAndFetchSagaStorageFrame<,>).CloseAndBuildAs<ISagaStorageFrame>(idType, sagaType).SimpleVariable;
    }
}