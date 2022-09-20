using System;
using System.Collections.Generic;
using System.Threading;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Marten;

namespace Wolverine.Marten.Persistence.Sagas;

internal class LoadDocumentFrame : AsyncFrame
{
    private readonly Type _sagaType;
    private readonly Variable _sagaId;
    private Variable _session;
    private Variable _cancellation;

    public LoadDocumentFrame(Type sagaType, Variable sagaId)
    {
        _sagaType = sagaType;
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
        writer.Write($"var {Saga.Usage} = await {_session.Usage}.LoadAsync<{Saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {_cancellation.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }


}