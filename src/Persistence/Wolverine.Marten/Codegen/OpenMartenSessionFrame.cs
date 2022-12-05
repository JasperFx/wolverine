using System;
using System.Collections.Generic;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;
using Wolverine.Marten.Publishing;

namespace Wolverine.Marten.Codegen;

internal class OpenMartenSessionFrame : AsyncFrame
{
    private Variable? _context;
    private Variable? _factory;

    public OpenMartenSessionFrame(Type sessionType)
    {
        ReturnVariable = new Variable(sessionType, this);
    }

    public Variable ReturnVariable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var methodName = ReturnVariable.VariableType == typeof(IQuerySession)
            ? nameof(OutboxedSessionFactory.QuerySession)
            : nameof(OutboxedSessionFactory.OpenSession);
        writer.Write($"await using var {ReturnVariable.Usage} = {_factory!.Usage}.{methodName}({_context!.Usage});");

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(IMessageContext));
        yield return _context;

        _factory = chain.FindVariable(typeof(OutboxedSessionFactory));
        yield return _factory;
    }
}