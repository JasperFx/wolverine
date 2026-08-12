using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Applies middleware to Wolverine message actions to apply a workflow with concurrency protections for
///     "command" messages that use an event sourced model to "decide" what new events to persist to that
///     model's stream.
/// </summary>
/// <remarks>
///     <para>
///     The name is deliberate: this is the <i>decider function</i> of the functional event sourcing
///     vocabulary — <c>decide(command, state) -&gt; events</c>. Wolverine supplies the <c>state</c> by
///     loading the model, and appends whatever <c>events</c> the method returns.
///     </para>
///     <para>
///     Store-agnostic — it works against whichever event store integration claims the model type. Where
///     <see cref="WriteModelAttribute" /> marks a single parameter, this marks the whole method (or class)
///     and reads the model's identity off the incoming command instead.
///     </para>
///     <para>
///     <c>Wolverine.Marten.AggregateHandlerAttribute</c> and <c>Wolverine.Polecat.AggregateHandlerAttribute</c>
///     both derive from this and are kept as-is for existing code. GH-3907.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class DeciderFunctionAttribute : ModifyChainAttribute, IDataRequirement, IMayInferMessageIdentity
{
    public DeciderFunctionAttribute(ModelConcurrencyStyle loadStyle)
    {
        LoadStyle = loadStyle;
    }

    public DeciderFunctionAttribute() : this(ModelConcurrencyStyle.Optimistic)
    {
    }

    internal ModelConcurrencyStyle LoadStyle { get; }

    /// <summary>
    ///     If true, the event store will enforce an optimistic concurrency check on this stream even if no
    ///     events are appended at the time of calling SaveChangesAsync(). This is useful when you want
    ///     to ensure the stream version has not advanced since it was fetched, even if the command
    ///     handler decides not to emit any new events.
    /// </summary>
    public bool AlwaysEnforceConsistency { get; set; }

    /// <summary>
    ///     Override the name of the member on the command type used to find the expected stream version
    ///     for optimistic concurrency checks. By default, Wolverine looks for a member named "Version"
    ///     of type int or long. This is useful in multi-stream operations where each stream needs
    ///     its own version source.
    /// </summary>
    public string? VersionSource { get; set; }

    /// <summary>
    ///     Override or "help" Wolverine to understand which type is the event sourced model type
    /// </summary>
    public Type? AggregateType { get; set; }

    internal MemberInfo? AggregateIdMember { get; set; }
    internal Type? CommandType { get; private set; }

    public MemberInfo? VersionMember { get; private set; }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        _onMissing ??= container.GetInstance<WolverineOptions>().EntityDefaults.OnMissing;

        // ReSharper disable once CanSimplifyDictionaryLookupWithTryAdd
        if (chain.Tags.ContainsKey(nameof(DeciderFunctionAttribute)))
        {
            return;
        }

        chain.Tags.Add(nameof(DeciderFunctionAttribute), "true");

        CommandType = chain.InputType();
        if (CommandType == null)
        {
            throw new InvalidOperationException(
                $"Cannot apply the aggregate handler workflow to chain {chain} because it has no input type");
        }

        // GH-3907: resolved out of the persistence strategies already registered on these GenerationRules,
        // so nothing here names a store. The model type has to be settled first, because that is what
        // decides which store owns it.
        var aggregateType = AggregateType ?? AggregateHandling.DetermineAggregateType(chain);
        AggregateType = aggregateType;

        var provider = ResolveEventSourcingProvider(rules, container, aggregateType);

        (AggregateIdMember, VersionMember) =
            AggregateHandling.DetermineAggregateIdAndVersion(AggregateType, CommandType, container, provider,
                VersionSource);

        var aggregateFrame = new MemberAccessFrame(CommandType, AggregateIdMember,
            $"{Variable.DefaultArgName(AggregateType)}_Id");

        var versionFrame = VersionMember == null
            ? null
            : new MemberAccessFrame(CommandType, VersionMember, $"{Variable.DefaultArgName(CommandType)}_Version");

        var handling = new AggregateHandling(this)
        {
            AggregateType = AggregateType,
            AggregateId = aggregateFrame.Variable,
            Provider = provider,
            LoadStyle = LoadStyle,
            Version = versionFrame?.Variable,
            AlwaysEnforceConsistency = AlwaysEnforceConsistency
        };

        handling.Apply(chain, container);
    }

    /// <summary>
    ///     The event store integration that owns <paramref name="modelType" />.
    /// </summary>
    /// <remarks>
    ///     The default resolves it out of the persistence strategies registered on
    ///     <see cref="GenerationRules" /> — the same registry <c>[Entity]</c> resolves through — which is
    ///     what makes this attribute store-agnostic.
    ///     <para>
    ///     A store's own attribute overrides this to name itself instead. That is not just belt and
    ///     braces: <c>AddMarten(...)</c> without <c>IntegrateWithWolverine()</c> is a supported
    ///     configuration, and in it nothing ever registers a persistence strategy — yet
    ///     <c>[WriteAggregate]</c> worked there before GH-3907 because it named its provider directly.
    ///     Overriding keeps that true. GH-3907.
    ///     </para>
    /// </remarks>
    protected virtual IEventSourcingFrameProvider ResolveEventSourcingProvider(GenerationRules rules,
        IServiceContainer container, Type modelType)
    {
        return rules.FindEventSourcingFrameProvider(container, modelType);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Handler/command/model types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Handler/command/model types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    public bool TryInferMessageIdentity(IChain chain, [NotNullWhen(true)] out PropertyInfo? property)
    {
        var inputType = chain.InputType();
        property = null;

        // This is gross
        if (inputType.Closes(typeof(IEvent<>)))
        {
            if (AggregateHandling.TryLoad(chain, out var handling))
            {
                property = handling.AggregateId.VariableType == typeof(string)
                    ? inputType!.GetProperty(nameof(IEvent.StreamKey))
                    : inputType!.GetProperty(nameof(IEvent.StreamId));
            }

            return property != null;
        }

        // No GenerationRules here, so there is no store to consult. AggregateHandling falls back to the
        // shared JasperFx.IdentityAttribute plus the naming conventions, which is what every store honors;
        // a store-specific [Identity] spelling only matters on the codegen path above, where the provider
        // is available.
        var aggregateType = AggregateType ?? AggregateHandling.DetermineAggregateType(chain);
        var idMember = AggregateHandling.DetermineAggregateIdMember(aggregateType, inputType!);
        property = idMember as PropertyInfo;
        return property != null;
    }

    private OnMissing? _onMissing;

    public bool Required { get; set; }
    public string MissingMessage { get; set; } = null!;

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }
}
