using System.Diagnostics;
using JasperFx.CodeGeneration;

namespace Wolverine.Persistence.Sagas;

public class IndeterminateSagaStateIdException : Exception
{
    public IndeterminateSagaStateIdException(Envelope envelope) : base(
        $"Could not determine a valid saga state id for Envelope {envelope}")
    {
    }
}

public class UnknownSagaException : Exception
{
    public UnknownSagaException(Type sagaStateType, object stateId) : base(
        $"Could not find an expected saga document of type {sagaStateType.FullNameInCode()} for id '{stateId}'")
    {
        Debug.WriteLine("something");
    }
}