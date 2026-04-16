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

        Next?.GenerateCode(method, writer);
    }
}
