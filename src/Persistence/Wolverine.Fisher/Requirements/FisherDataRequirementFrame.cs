using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Fisher;
using Wolverine.Configuration;
using Wolverine.Middleware;
using Wolverine.Fisher.Codegen;

namespace Wolverine.Fisher.Requirements;

/// <summary>
/// Code generation frame that evaluates an <see cref="IFisherDataRequirement"/> (or an
/// <see cref="IEnumerable{IFisherDataRequirement}"/>) returned from a "Before" / "Validate"
/// method on a Wolverine handler. Implements <see cref="IBatchableFrame"/> so the
/// <see cref="FisherBatchingPolicy"/> can fold the requirement's existence check into a
/// single Fisher <see cref="Fisher.Batching.IBatchedQuery"/> alongside other batchable
/// operations on the same chain (e.g., <c>[ReadAggregate]</c> entity loads, <c>[WriteAggregate]</c>
/// fetches).
/// </summary>
internal class FisherDataRequirementFrame : AsyncFrame, IBatchableFrame
{
    private static int _count;
    private readonly Variable _requirementVariable;
    private readonly bool _isEnumerable;
    private readonly int _id;
    private Variable? _session;
    private Variable? _logger;
    private Variable? _cancellation;
    private Variable? _batchQuery;

    public FisherDataRequirementFrame(Variable requirementVariable, bool isEnumerable)
    {
        _requirementVariable = requirementVariable;
        _isEnumerable = isEnumerable;
        _id = ++_count;
        uses.Add(requirementVariable);
    }

    private string MaterializedVarName => $"materializedFisherDataReqs{_id}";

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        if (_isEnumerable)
        {
            writer.WriteLine(
                $"var {MaterializedVarName} = new {typeof(List<IFisherDataRequirement>).FullNameInCode()}();");
            writer.Write($"BLOCK:foreach (var dataReq{_id} in {_requirementVariable.Usage})");
            writer.WriteLine(
                $"dataReq{_id}.{nameof(IFisherDataRequirement.RegisterInBatch)}({_batchQuery!.Usage});");
            writer.WriteLine($"{MaterializedVarName}.Add(dataReq{_id});");
            writer.FinishBlock();
        }
        else
        {
            writer.WriteLine(
                $"{_requirementVariable.Usage}.{nameof(IFisherDataRequirement.RegisterInBatch)}({_batchQuery!.Usage});");
        }
    }

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQuery = batchQuery;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Evaluate Fisher data requirement(s)");

        if (_batchQuery != null)
        {
            // Batched mode — use CheckFromBatch after FisherBatchFrame ran the IBatchedQuery
            if (_isEnumerable)
            {
                writer.Write($"BLOCK:foreach (var dataReqCheck{_id} in {MaterializedVarName})");
                writer.WriteLine(
                    $"await dataReqCheck{_id}.{nameof(IFisherDataRequirement.CheckFromBatch)}({_logger!.Usage});");
                writer.FinishBlock();
            }
            else
            {
                writer.WriteLine(
                    $"await {_requirementVariable.Usage}.{nameof(IFisherDataRequirement.CheckFromBatch)}({_logger!.Usage});");
            }
        }
        else
        {
            // Non-batched mode — single requirement on the chain, hit the session directly
            if (_isEnumerable)
            {
                writer.Write($"BLOCK:foreach (var dataReq{_id} in {_requirementVariable.Usage})");
                writer.WriteLine(
                    $"await dataReq{_id}.{nameof(IFisherDataRequirement.CheckAsync)}({_session!.Usage}, {_logger!.Usage}, {_cancellation!.Usage});");
                writer.FinishBlock();
            }
            else
            {
                writer.WriteLine(
                    $"await {_requirementVariable.Usage}.{nameof(IFisherDataRequirement.CheckAsync)}({_session!.Usage}, {_logger!.Usage}, {_cancellation!.Usage});");
            }
        }

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Continuation strategy that recognises a "Before" / "Validate" method returning an
/// <see cref="IFisherDataRequirement"/> or <see cref="IEnumerable{IFisherDataRequirement}"/>
/// and inserts a <see cref="FisherDataRequirementFrame"/> to evaluate it. Registered on the
/// Wolverine codegen pipeline by <see cref="FisherIntegration"/>.
/// </summary>
public class FisherDataRequirementContinuationStrategy : IContinuationStrategy
{
    public bool TryFindContinuationHandler(IChain chain, MethodCall call, out Frame? frame)
    {
        // Single IFisherDataRequirement return
        var singleVar =
            call.Creates.FirstOrDefault(v => v.VariableType == typeof(IFisherDataRequirement));
        if (singleVar != null)
        {
            frame = new FisherDataRequirementFrame(singleVar, isEnumerable: false);
            return true;
        }

        // IEnumerable<IFisherDataRequirement> return
        var enumerableType = typeof(IEnumerable<IFisherDataRequirement>);
        var enumerableVar = call.Creates.FirstOrDefault(v =>
            v.VariableType == enumerableType || v.VariableType.CanBeCastTo(enumerableType));
        if (enumerableVar != null)
        {
            frame = new FisherDataRequirementFrame(enumerableVar, isEnumerable: true);
            return true;
        }

        frame = null;
        return false;
    }
}
