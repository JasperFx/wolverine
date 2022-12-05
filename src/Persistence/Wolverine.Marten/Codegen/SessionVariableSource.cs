using System;
using JasperFx.CodeGeneration.Model;
using Marten;

namespace Wolverine.Marten.Codegen;

internal class SessionVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IQuerySession) || type == typeof(IDocumentSession);
    }

    public Variable Create(Type type)
    {
        return new OpenMartenSessionFrame(type).ReturnVariable;
    }
}