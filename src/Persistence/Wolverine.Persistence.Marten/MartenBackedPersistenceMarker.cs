using System;
using Wolverine.Persistence.Postgresql;
using LamarCodeGeneration.Model;

namespace Wolverine.Persistence.Marten;

internal class MartenBackedPersistenceMarker : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == GetType();
    }

    public Variable Create(Type type)
    {
        return Variable.For<PostgresqlEnvelopePersistence>();
    }
}
