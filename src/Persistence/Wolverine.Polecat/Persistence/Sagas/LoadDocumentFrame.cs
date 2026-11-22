using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat;

namespace Wolverine.Polecat.Persistence.Sagas;

internal class LoadDocumentFrame : AsyncFrame
{
    public const string ExpectedSagaRevision = "expectedSagaRevision";

    private readonly Variable _sagaId;
    private readonly string? _valuePropertyName;
    private Variable? _cancellation;
    private Variable? _session;

    public LoadDocumentFrame(Type sagaType, Variable sagaId)
    {
        // Detect StronglyTypedId but do NOT create MemberAccessVariable here
        // (that would introduce a circular dependency in the variable resolution graph).
        // Instead, record the property name and unwrap at code generation time.
        if (sagaId.VariableType != typeof(Guid) && sagaId.VariableType != typeof(string) &&
            sagaId.VariableType != typeof(int) && sagaId.VariableType != typeof(long))
        {
            var valueType = ValueTypeInfo.ForType(sagaId.VariableType);
            if (valueType != null)
            {
                _valuePropertyName = valueType.ValueProperty.Name;
            }
        }

        _sagaId = sagaId;
        uses.Add(sagaId);

        var usage = $"{Variable.DefaultArgName(sagaType)}_{sagaId.Usage.Split('.').Last()}";
        Saga = new Variable(sagaType, usage, this);
    }

    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _sagaId;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Try to load the existing saga document");

        // For StronglyTypedId, unwrap to the underlying value (e.g., sagaId.Value)
        var idExpression = _valuePropertyName != null
            ? $"{_sagaId.Usage}.{_valuePropertyName}"
            : _sagaId.Usage;

        writer.Write(
            $"var {Saga.Usage} = await {_session!.Usage}.LoadAsync<{Saga.VariableType.FullNameInCode()}>({idExpression}, {_cancellation!.Usage}).ConfigureAwait(false);");

        // GH-5658: a revisioned saga is updated at the revision after the one it was loaded with, the way
        // Wolverine's Marten saga storage does it. Polecat turns on numeric revisions for an IRevisioned
        // document, so a plain Update() of the loaded saga fails its concurrency check.
        if (Saga.VariableType.CanBeCastTo<IRevisioned>() && Saga.VariableType.CanBeCastTo<Saga>())
        {
            writer.WriteComment($"{Saga.VariableType.FullNameInCode()} implements {typeof(IRevisioned).FullNameInCode()}, so Wolverine will try to update based on the revision as a concurrency protection");
            writer.Write($"var {ExpectedSagaRevision} = 0;");
            writer.Write($"BLOCK:if ({Saga.Usage} != null)");
            writer.Write($"{ExpectedSagaRevision} = {Saga.Usage}.{nameof(IRevisioned.Version)} + 1;");
            writer.FinishBlock();
        }

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
///     GH-5658: stores an <see cref="IRevisioned" /> saga at the revision captured by <see cref="LoadDocumentFrame" />
/// </summary>
internal class UpdateSagaRevisionFrame : SyncFrame
{
    private Variable? _session;

    public UpdateSagaRevisionFrame(Variable saga)
    {
        Saga = saga;
    }

    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Register the document operation with the current session against the expected *final* revision");
        writer.Write($"{_session!.Usage}.{nameof(IDocumentOperations.UpdateRevision)}({Saga.Usage}, {LoadDocumentFrame.ExpectedSagaRevision});");
        Next?.GenerateCode(method, writer);
    }
}
