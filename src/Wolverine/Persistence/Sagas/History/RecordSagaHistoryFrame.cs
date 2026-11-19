using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
///     GH-5665. Records the message in the history of a saga that keeps one (<see cref="RecordSagaHistoryAttribute" />),
///     right before the saga is saved, so the row is written in the saga's own transaction
/// </summary>
internal class RecordSagaHistoryFrame : AsyncFrame
{
    private readonly Type _sagaType;
    private readonly Variable? _saga;
    private readonly Variable? _sagaId;
    private readonly MemberInfo? _sagaIdMember;
    private readonly string _handler;
    private readonly SagaHistoryStep _step;
    private readonly bool _retained;
    private Variable? _context;
    private Variable? _cancellation;

    /// <param name="sagaType">The saga type</param>
    /// <param name="saga">The saga, when there is one</param>
    /// <param name="sagaId">The saga id variable; when null, the id is read from <paramref name="saga" /></param>
    /// <param name="sagaIdMember">The saga's identity member, to read the id from the saga</param>
    /// <param name="handler">The saga methods that handled the message</param>
    /// <param name="step">Which part of the generated handler this is</param>
    /// <param name="retained">Whether completed sagas of this type are retained</param>
    public RecordSagaHistoryFrame(Type sagaType, Variable? saga, Variable? sagaId, MemberInfo? sagaIdMember,
        string handler, SagaHistoryStep step, bool retained)
    {
        _sagaType = sagaType;
        _saga = saga;
        _sagaId = sagaId;
        _sagaIdMember = sagaIdMember;
        _handler = handler;
        _step = step;
        _retained = retained;

        if (saga != null) uses.Add(saga);
        if (sagaId != null) uses.Add(sagaId);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    private string identityUsage(bool fsharp)
    {
        if (_sagaId != null) return fsharp ? $"box {_sagaId.FSharpUsage}" : _sagaId.Usage;
        if (_saga != null && _sagaIdMember != null)
        {
            return fsharp ? $"box {_saga.FSharpUsage}.{_sagaIdMember.Name}" : $"{_saga.Usage}.{_sagaIdMember.Name}";
        }

        return "null";
    }

    private string handlerLiteral => "\"" + _handler.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Record the message in the saga's history, in the saga's own transaction");
        writer.Write(
            $"await {typeof(SagaHistory).FullNameInCode()}.{nameof(SagaHistory.RecordAsync)}({_context!.Usage}, typeof({_sagaType.FullNameInCode()}), {identityUsage(false)}, {(_saga?.Usage ?? "null")}, {handlerLiteral}, {typeof(SagaHistoryStep).FullNameInCode()}.{_step}, {(_retained ? "true" : "false")}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    // `await ...` -> F# `do! ...` inside the task { } computation expression
    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("// Record the message in the saga's history, in the saga's own transaction");
        writer.WriteLine(
            $"do! {typeof(SagaHistory).FSharpName()}.{nameof(SagaHistory.RecordAsync)}({_context!.FSharpUsage}, typeof<{_sagaType.FSharpName()}>, {identityUsage(true)}, {(_saga == null ? "null" : _saga.FSharpUsage)}, {handlerLiteral}, {typeof(SagaHistoryStep).FSharpName()}.{_step}, {(_retained ? "true" : "false")}, {_cancellation!.FSharpUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }
}
