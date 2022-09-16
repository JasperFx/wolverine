using System;
using LamarCodeGeneration.Model;

namespace Wolverine.Persistence.Database;

public class DatabaseBackedPersistenceMarker : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == GetType();
    }

    public Variable Create(Type type)
    {
        return Variable.For<IDatabaseBackedEnvelopePersistence>();
    }
}
