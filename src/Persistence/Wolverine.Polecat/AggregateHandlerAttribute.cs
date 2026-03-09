using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Polecat;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Polecat.Codegen;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Partitioning;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Polecat;

/// <summary>
///     Applies middleware to Wolverine message actions to apply a workflow with concurrency protections for
///     "command" messages that use a Polecat projected aggregate to "decide" what
///     on new events to persist to the aggregate stream.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AggregateHandlerAttribute : ModifyChainAttribute, IDataRequirement, IMayInferMessageIdentity
{
    public AggregateHandlerAttribute(ConcurrencyStyle loadStyle)
    {
        LoadStyle = loadStyle;
    }

    public AggregateHandlerAttribute() : this(ConcurrencyStyle.Optimistic)
    {
    }

    internal ConcurrencyStyle LoadStyle { get; }

    public bool AlwaysEnforceConsistency { get; set; }
    public string? VersionSource { get; set; }
    public Type? AggregateType { get; set; }
    internal MemberInfo? AggregateIdMember { get; set; }
    internal Type? CommandType { get; private set; }
    public MemberInfo? VersionMember { get; private set; }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        _onMissing ??= container.GetInstance<WolverineOptions>().EntityDefaults.OnMissing;

        if (chain.Tags.ContainsKey(nameof(AggregateHandlerAttribute)))
        {
            return;
        }

        chain.Tags.Add(nameof(AggregateHandlerAttribute), "true");

        CommandType = chain.InputType();
        if (CommandType == null)
        {
            throw new InvalidOperationException(
                $"Cannot apply Polecat aggregate handler workflow to chain {chain} because it has no input type");
        }

        AggregateType ??= AggregateHandling.DetermineAggregateType(chain);

        (AggregateIdMember, VersionMember) =
            AggregateHandling.DetermineAggregateIdAndVersion(AggregateType, CommandType, container, VersionSource);

        var aggregateFrame = new MemberAccessFrame(CommandType, AggregateIdMember,
            $"{Variable.DefaultArgName(AggregateType)}_Id");

        var versionFrame = VersionMember == null ? null : new MemberAccessFrame(CommandType, VersionMember, $"{Variable.DefaultArgName(CommandType)}_Version");

        var handling = new AggregateHandling(this)
        {
            AggregateType = AggregateType,
            AggregateId = aggregateFrame.Variable,
            LoadStyle = LoadStyle,
            Version = versionFrame?.Variable,
            AlwaysEnforceConsistency = AlwaysEnforceConsistency
        };

        handling.Apply(chain, container);
    }

    public bool TryInferMessageIdentity(IChain chain, out PropertyInfo property)
    {
        var inputType = chain.InputType();
        property = default!;

        if (inputType.Closes(typeof(IEvent<>)))
        {
            if (AggregateHandling.TryLoad(chain, out var handling))
            {
                property = handling.AggregateId.VariableType == typeof(string)
                    ? inputType.GetProperty(nameof(IEvent.StreamKey))
                    : inputType.GetProperty(nameof(IEvent.StreamId));
            }

            return property != null;
        }

        var aggregateType = AggregateType ?? AggregateHandling.DetermineAggregateType(chain);
        var idMember = AggregateHandling.DetermineAggregateIdMember(aggregateType, inputType);
        property = idMember as PropertyInfo;
        return property != null;
    }

    private OnMissing? _onMissing;

    public bool Required { get; set; }
    public string MissingMessage { get; set; }

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }
}
