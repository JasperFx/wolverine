using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;

namespace Wolverine.Marten.Codegen;

internal class LoadAggregateFrame : AsyncFrame,  IBatchableFrame
{
    private readonly AggregateHandling _att;
    private Variable? _session;
    private Variable? _token;
    private Variable? _batchQuery;
    private Variable? _batchQueryItem;
    private readonly Variable _identity;
    private readonly Variable? _version;
    private readonly Type _eventStreamType;
    private readonly Variable _rawIdentity;


    public LoadAggregateFrame(AggregateHandling att)
    {
        _att = att;
        _identity = _att.AggregateId;

        if (_att is { LoadStyle: ConcurrencyStyle.Optimistic, Version: not null })
        {
            _version = _att.Version;
        }

        _eventStreamType = typeof(IEventStream<>).MakeGenericType(_att.AggregateType);
        Stream = new Variable(_eventStreamType, this);

        _rawIdentity = _identity;
        // For natural keys, keep the full natural key object (don't unwrap)
        if (!_att.IsNaturalKey && _rawIdentity.VariableType != typeof(Guid) && _rawIdentity.VariableType != typeof(string))
        {
            var valueType = ValueTypeInfo.ForType(_rawIdentity.VariableType);
            _rawIdentity = new MemberAccessVariable(_identity, valueType.ValueProperty);
        }
    }

    public Variable Stream { get; }
    public bool IsNaturalKey => _att.IsNaturalKey;

    private string NaturalKeyFetchForWriting(string targetUsage, string? cancellationTokenUsage = null)
    {
        var aggType = _att.AggregateType.FullNameInCode();
        var nkType = _identity.VariableType.FullNameInCode();
        var args = cancellationTokenUsage != null
            ? $"{_identity.Usage}, {cancellationTokenUsage}"
            : _identity.Usage;
        return $"{targetUsage}.Events.FetchForWriting<{aggType}, {nkType}>({args})";
    }

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        if (_att.IsNaturalKey)
        {
            writer.WriteLine($"var {_batchQueryItem!.Usage} = {NaturalKeyFetchForWriting(_batchQuery!.Usage)};");
            return;
        }

        if (_att.LoadStyle == ConcurrencyStyle.Exclusive)
        {
            writer.WriteLine($"var {_batchQueryItem!.Usage} = {_batchQuery!.Usage}.Events.FetchForExclusiveWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage});");
        }
        else if (_version == null)
        {
            writer.WriteLine($"var {_batchQueryItem!.Usage} = {_batchQuery!.Usage}.Events.FetchForWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage});");
        }
        else
        {
            writer.WriteLine($"var {_batchQueryItem!.Usage} = {_batchQuery!.Usage}.Events.FetchForWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage}, {_version.Usage});");
        }
    }

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQueryItem = new Variable(typeof(Task<>).MakeGenericType(_eventStreamType), Stream.Usage + "_BatchItem",
            this);
        _batchQuery = batchQuery;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _identity;
        if (_version != null) yield return _version;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }
    }

    /// <summary>
    /// Set to true after GenerateCodeForBatchResolution has been called,
    /// to prevent duplicate variable declarations.
    /// </summary>
    internal bool BatchResolutionGenerated { get; private set; }

    /// <summary>
    /// When this frame is part of a batch query, generate only the code that
    /// resolves the batch item result (var stream_entity = await ...BatchItem).
    /// This is called by MartenBatchFrame after the batch execute, ensuring
    /// the variable is declared before any guard frames reference it.
    /// </summary>
    public void GenerateCodeForBatchResolution(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null) return;

        writer.WriteComment("Loading Marten aggregate as part of the aggregate handler workflow");
        writer.Write(
            $"var {Stream.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");

        if (_att.AlwaysEnforceConsistency)
        {
            writer.WriteLine($"{Stream.Usage}.{nameof(IEventStream<string>.AlwaysEnforceConsistency)} = true;");
        }

        BatchResolutionGenerated = true;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // If batch resolution already generated the variable declaration, skip
        if (BatchResolutionGenerated)
        {
            Next?.GenerateCode(method, writer);
            return;
        }

        writer.WriteComment("Loading Marten aggregate as part of the aggregate handler workflow");
        if (_batchQueryItem == null)
        {
            if (_att.IsNaturalKey)
            {
                writer.WriteLine($"var {Stream.Usage} = await {NaturalKeyFetchForWriting(_session!.Usage, _token!.Usage)};");
            }
            else if (_att.LoadStyle == ConcurrencyStyle.Exclusive)
            {
                writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForExclusiveWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage}, {_token!.Usage});");
            }
            else if (_version == null)
            {
                writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage}, {_token!.Usage});");
            }
            else
            {
                writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage}, {_version.Usage}, {_token!.Usage});");
            }
        }
        else
        {
            writer.Write(
                $"var {Stream.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");
        }

        if (_att.AlwaysEnforceConsistency)
        {
            writer.WriteLine($"{Stream.Usage}.{nameof(IEventStream<string>.AlwaysEnforceConsistency)} = true;");
        }

        Next?.GenerateCode(method, writer);
    }
}
