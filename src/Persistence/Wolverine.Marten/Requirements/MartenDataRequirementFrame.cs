using System.Collections.Generic;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Middleware;

namespace Wolverine.Marten.Requirements;

internal class MartenDataRequirementFrame : AsyncFrame, IBatchableFrame
{
    private static int _count;
    private readonly Variable _requirementVariable;
    private readonly bool _isEnumerable;
    private readonly int _id;
    private Variable? _session;
    private Variable? _logger;
    private Variable? _cancellation;
    private Variable? _batchQuery;

    public MartenDataRequirementFrame(Variable requirementVariable, bool isEnumerable)
    {
        _requirementVariable = requirementVariable;
        _isEnumerable = isEnumerable;
        _id = ++_count;
        uses.Add(requirementVariable);
    }

    private string MaterializedVarName => $"materializedDataReqs{_id}";

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        if (_isEnumerable)
        {
            writer.WriteLine(
                $"var {MaterializedVarName} = new {typeof(List<IMartenDataRequirement>).FullNameInCode()}();");
            writer.Write($"BLOCK:foreach (var dataReq{_id} in {_requirementVariable.Usage})");
            writer.WriteLine(
                $"dataReq{_id}.{nameof(IMartenDataRequirement.RegisterInBatch)}({_batchQuery!.Usage});");
            writer.WriteLine($"{MaterializedVarName}.Add(dataReq{_id});");
            writer.FinishBlock();
        }
        else
        {
            writer.WriteLine(
                $"{_requirementVariable.Usage}.{nameof(IMartenDataRequirement.RegisterInBatch)}({_batchQuery!.Usage});");
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
        writer.WriteComment("Evaluate Marten data requirement(s)");

        if (_batchQuery != null)
        {
            // Batched mode - use CheckFromBatch after batch execution
            if (_isEnumerable)
            {
                writer.Write($"BLOCK:foreach (var dataReqCheck{_id} in {MaterializedVarName})");
                writer.WriteLine(
                    $"await dataReqCheck{_id}.{nameof(IMartenDataRequirement.CheckFromBatch)}({_logger!.Usage});");
                writer.FinishBlock();
            }
            else
            {
                writer.WriteLine(
                    $"await {_requirementVariable.Usage}.{nameof(IMartenDataRequirement.CheckFromBatch)}({_logger!.Usage});");
            }
        }
        else
        {
            // Non-batched mode - use CheckAsync
            if (_isEnumerable)
            {
                writer.Write($"BLOCK:foreach (var dataReq{_id} in {_requirementVariable.Usage})");
                writer.WriteLine(
                    $"await dataReq{_id}.{nameof(IMartenDataRequirement.CheckAsync)}({_session!.Usage}, {_logger!.Usage}, {_cancellation!.Usage});");
                writer.FinishBlock();
            }
            else
            {
                writer.WriteLine(
                    $"await {_requirementVariable.Usage}.{nameof(IMartenDataRequirement.CheckAsync)}({_session!.Usage}, {_logger!.Usage}, {_cancellation!.Usage});");
            }
        }

        Next?.GenerateCode(method, writer);
    }
}

public class MartenDataRequirementContinuationStrategy : IContinuationStrategy
{
    public bool TryFindContinuationHandler(IChain chain, MethodCall call, out Frame? frame)
    {
        // Check for single IMartenDataRequirement return
        var singleVar =
            call.Creates.FirstOrDefault(v => v.VariableType == typeof(IMartenDataRequirement));
        if (singleVar != null)
        {
            frame = new MartenDataRequirementFrame(singleVar, isEnumerable: false);
            return true;
        }

        // Check for IEnumerable<IMartenDataRequirement> return
        var enumerableType = typeof(IEnumerable<IMartenDataRequirement>);
        var enumerableVar = call.Creates.FirstOrDefault(v =>
            v.VariableType == enumerableType || v.VariableType.CanBeCastTo(enumerableType));
        if (enumerableVar != null)
        {
            frame = new MartenDataRequirementFrame(enumerableVar, isEnumerable: true);
            return true;
        }

        frame = null;
        return false;
    }
}
