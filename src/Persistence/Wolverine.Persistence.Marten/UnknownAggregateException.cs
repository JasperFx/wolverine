using System;
using LamarCodeGeneration;

namespace Wolverine.Persistence.Marten;

public class UnknownAggregateException : Exception
{
    public UnknownAggregateException(Type aggregateType, object id) : base($"Could not find an aggregate of type {aggregateType.FullNameInCode()} with id {id}")
    {
    }
}
