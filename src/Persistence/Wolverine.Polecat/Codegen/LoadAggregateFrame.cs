using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat;
using Polecat.Events;

namespace Wolverine.Polecat.Codegen;

internal class LoadAggregateFrame : AsyncFrame
{
    private readonly AggregateHandling _att;
    private Variable? _session;
    private Variable? _token;
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

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _identity;
        if (_version != null) yield return _version;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Loading Polecat aggregate as part of the aggregate handler workflow");
        if (_att.IsNaturalKey)
        {
            var aggType = _att.AggregateType.FullNameInCode();
            var nkType = _identity.VariableType.FullNameInCode();
            writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForWriting<{aggType}, {nkType}>({_identity.Usage}, {_token!.Usage});");
        }
        else if (_att.LoadStyle == ConcurrencyStyle.Exclusive)
        {
            writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForExclusiveWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage}, {_token.Usage});");
        }
        else if (_version == null)
        {
            writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage}, {_token.Usage});");
        }
        else
        {
            writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage}, {_version.Usage}, {_token.Usage});");
        }

        if (_att.AlwaysEnforceConsistency)
        {
            writer.WriteLine($"{Stream.Usage}.{nameof(IEventStream<string>.AlwaysEnforceConsistency)} = true;");
        }

        Next?.GenerateCode(method, writer);
    }
}
