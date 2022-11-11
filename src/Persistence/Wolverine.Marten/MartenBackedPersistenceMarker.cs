using System;
using Wolverine.Postgresql;
using LamarCodeGeneration.Model;

namespace Wolverine.Marten;

internal class MartenBackedPersistenceMarker : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == GetType();
    }

    public Variable Create(Type type)
    {
        return Variable.For<PostgresqlMessageStore>();
    }
}
