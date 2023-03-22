using System;
using System.Collections.Generic;
using System.Threading;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;

namespace Wolverine.Marten.Persistence.Sagas;

internal class LoadDocumentFrame : AsyncFrame
{
    private readonly Variable _sagaId;
    private Variable? _cancellation;
    private Variable? _session;

    public LoadDocumentFrame(Type sagaType, Variable sagaId)
    {
        _sagaId = sagaId;

        Saga = new Variable(sagaType, this);
    }

    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Try to load the existing saga document");
        writer.Write(
            $"var {Saga.Usage} = await {_session!.Usage}.LoadAsync<{Saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}